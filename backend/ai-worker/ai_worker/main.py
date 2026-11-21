"""AI worker main loop: Kafka records -> jobs -> one Claude tool-use loop per job (OVERVIEW "Architecture").

One thread: the loop polls Kafka between jobs, so a long job delays consumption but loses nothing (enqueue is
idempotent and offsets are committed only after the job row is inserted).
"""

import json
import logging
import os
import signal
import sys
from pathlib import Path
from types import SimpleNamespace

import psycopg
from confluent_kafka import OFFSET_BEGINNING, Consumer, KafkaException, TopicPartition

from . import agent, jobs, mapping, runbooks
from .agent import JobFailed
from .api import Api, ApiError

log = logging.getLogger("ai_worker")
SAMPLES_TOPIC, CONFIG_TOPIC, CHANGES_TOPIC = "schema.samples", "config.changes", "alerts.changes"
MIGRATIONS = Path(__file__).parent.parent / "migrations"
MIGRATION_LOCK = 0x61697772  # any constant shared by all replicas
BATCH = 100
IDLE_S, RETRY_S = 1.0, 5.0  # poll wait with no job to run, and after a retryable failure
MAX_POLL_INTERVAL_MS = 1_800_000  # a job (up to 6 model calls) runs between polls; a rebalance past it only redelivers

HANDLERS = {"mapping": mapping.handle, "runbook": runbooks.convert, "proposal": runbooks.propose}


def _decode(msg) -> dict | None:
    if msg.error():
        log.warning("skipped %s: kafka error %s", msg.topic(), msg.error())
        return None
    try:
        doc = json.loads(msg.value())
    except (TypeError, ValueError):
        doc = None
    if type(doc) is not dict:
        log.warning("skipped %s[%s]@%s: not a JSON object", msg.topic(), msg.partition(), msg.offset())
        return None
    return doc


def sample_job(d: dict) -> tuple | None:
    """events.md §8 record -> (job id, kind, input), or None."""
    fp, source_id = d.get("fingerprint"), d.get("source_id")
    if type(fp) is not str or not fp or type(source_id) is not str or "sample" not in d:
        return None
    return f"schema:{fp}", "mapping", {"fingerprint": fp, "source_id": source_id, "sample": d["sample"]}


def config_job(d: dict, mappings: dict) -> tuple | None:
    """events.md §6 record: keeps `mappings` (id -> body) current; a runbook draft not yet converted -> a job."""
    kind, rid, body = d.get("kind"), d.get("id"), d.get("body")
    if type(rid) is not str:
        return None
    if kind == "mapping":
        if d.get("deleted") is True:
            mappings.pop(rid, None)
        elif type(body) is dict:
            mappings[rid] = body
        return None
    if (kind == "runbook" and type(body) is dict and body.get("status") == "draft" and body.get("canonical") is None
            and type(body.get("version")) is int):
        return f"runbook:{rid}:{body['version']}", "runbook", body
    return None


def alert_job(d: dict) -> tuple | None:
    """events.md §4 record: a `resolved` change (by a person or `auto`) -> a proposal job for that alert."""
    alert = d.get("alert")
    alert_id = alert.get("id") if type(alert) is dict else None
    if d.get("change") != "resolved" or type(alert_id) is not str or not alert_id:
        return None
    return f"proposal:{alert_id}", "proposal", {"alert_id": alert_id}


class Worker:
    def __init__(self, pg, jobs_pg, samples, config, changes, api):
        self.pg, self.jobs_pg, self.api = pg, jobs_pg, api  # pg: autocommit, for enqueue; jobs_pg: claim transactions
        self.samples, self.config, self.changes = samples, config, changes
        self.mappings: dict[str, dict] = {}

    def _enqueue(self, job: tuple | None) -> None:
        if job and jobs.enqueue(self.pg, *job):
            log.info("enqueued %s", job[0])

    def drain_config(self) -> None:
        """Read `config.changes` from the beginning to its current end (no group, never committed)."""
        self.config.assign([TopicPartition(CONFIG_TOPIC, 0, OFFSET_BEGINNING)])
        _, high = self.config.get_watermark_offsets(TopicPartition(CONFIG_TOPIC, 0), timeout=10)
        pos = 0
        while pos < high:
            for msg in self.config.consume(num_messages=500, timeout=1.0):
                if not msg.error():
                    pos = msg.offset() + 1
                if d := _decode(msg):
                    self._enqueue(config_job(d, self.mappings))
        log.info("read %s to offset %d: %d mappings", CONFIG_TOPIC, high, len(self.mappings))

    def poll(self, timeout: float) -> None:
        for consumer, translate, wait in ((self.samples, sample_job, timeout), (self.changes, alert_job, 0),
                                          (self.config, lambda d: config_job(d, self.mappings), 0)):
            msgs = consumer.consume(num_messages=BATCH, timeout=wait)
            for msg in msgs:
                if d := _decode(msg):
                    self._enqueue(translate(d))
            if msgs and consumer is not self.config:
                try:  # after the inserts: a crash before this line means redelivery, which enqueue ignores
                    consumer.commit(asynchronous=False)
                except KafkaException as e:
                    log.warning("commit failed (%s); the records are redelivered", e)

    def run_one(self) -> float:
        """Claim and run one job; returns how long the next poll may wait."""
        job = jobs.claim(self.jobs_pg)
        if job is None:
            return IDLE_S
        log.info("running %s (attempt %d)", job["id"], job["attempts"] + 1)
        ctx = SimpleNamespace(api=self.api, mappings=list(self.mappings.values()),
                              other_samples=lambda: jobs.other_samples(self.jobs_pg, job["id"]),
                              run_agent=agent.run_agent)
        try:
            result = HANDLERS[job["kind"]](job["input"], ctx)
        except (JobFailed, ApiError) as e:
            log.warning("%s failed: %s", job["id"], e)
            jobs.finish(self.jobs_pg, job, error=str(e))
        except Exception as e:
            log.exception("%s failed, will retry", job["id"])
            jobs.finish(self.jobs_pg, job, error=repr(e), retry=True)
            return RETRY_S
        else:
            log.info("%s done", job["id"])
            jobs.finish(self.jobs_pg, job, result=result)
        return 0


def main() -> None:
    logging.basicConfig(level=os.environ.get("LOG_LEVEL", "INFO").upper(), stream=sys.stderr,
                        format="%(asctime)s %(levelname)s %(name)s %(message)s")
    if missing := [k for k in ("KAFKA_BROKERS", "DATABASE_URL", "API_URL") if not os.environ.get(k)]:
        sys.exit(f"ai-worker: missing env var {', '.join(missing)}")
    brokers, db_url = os.environ["KAFKA_BROKERS"], os.environ["DATABASE_URL"]

    with psycopg.connect(db_url, autocommit=True) as c:  # the lock is released when the connection closes
        c.execute("SELECT pg_advisory_lock(%s)", (MIGRATION_LOCK,))
        for f in sorted(MIGRATIONS.glob("*.sql")):
            c.execute(f.read_text())

    group = {"bootstrap.servers": brokers, "group.id": "ai-worker", "enable.auto.commit": False,
             "max.poll.interval.ms": MAX_POLL_INTERVAL_MS}
    samples = Consumer({**group, "auto.offset.reset": "earliest"})
    samples.subscribe([SAMPLES_TOPIC])
    changes = Consumer({**group, "auto.offset.reset": "latest"})
    changes.subscribe([CHANGES_TOPIC])
    # the client requires a group.id; assign() never joins the group and nothing is committed
    config = Consumer({"bootstrap.servers": brokers, "group.id": "ai-worker-config", "enable.auto.commit": False})
    worker = Worker(psycopg.connect(db_url, autocommit=True), psycopg.connect(db_url), samples, config, changes, Api())

    stop = []
    for s in (signal.SIGTERM, signal.SIGINT):
        signal.signal(s, lambda *_: stop.append(True))
    worker.drain_config()
    has_key = bool(os.environ.get("ANTHROPIC_API_KEY"))
    if not has_key:
        log.warning("ANTHROPIC_API_KEY is not set: jobs are enqueued but none is run")
    log.info("ai-worker started")
    wait = 0.0
    while not stop:
        worker.poll(wait)
        wait = worker.run_one() if has_key else IDLE_S
    for c in (samples, changes, config):
        c.close()
    log.info("ai-worker stopped")
