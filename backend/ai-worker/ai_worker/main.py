"""AI worker main loop: Kafka records -> jobs -> one Claude tool-use loop or one Jev call per job
(OVERVIEW "Architecture").

One thread: the loop polls Kafka between jobs, so a long job delays consumption but loses nothing (enqueue is
idempotent and offsets are committed only after the job row is inserted).
"""

import functools
import json
import logging
import os
import signal
import sys
import time
from datetime import UTC, datetime
from pathlib import Path
from types import SimpleNamespace

import psycopg
from confluent_kafka import OFFSET_BEGINNING, Consumer, KafkaException, TopicPartition
from langgraph.checkpoint.postgres import PostgresSaver
from psycopg.rows import dict_row

from . import agent, jobs, judge, mapping, runbooks, runs
from .agent import JobFailed
from .api import Api, ApiError

log = logging.getLogger("ai_worker")
SAMPLES_TOPIC, CONFIG_TOPIC, CHANGES_TOPIC = "schema.samples", "config.changes", "alerts.changes"
MIGRATIONS = Path(__file__).parent.parent / "migrations"
MIGRATION_LOCK = 0x61697772  # any constant shared by all replicas
BATCH = 100
IDLE_S, RETRY_S = 1.0, 5.0  # poll wait with no job to run, and after a retryable failure
MAX_POLL_INTERVAL_MS = 1_800_000  # a job (up to 6 model calls) runs between polls; a rebalance past it only redelivers

HANDLERS = {"mapping": mapping.handle, "runbook": runbooks.convert, "proposal": runbooks.propose,
            "severity": judge.handle, "run_risk": runs.risk, "run_start": runs.start}
KEYS = {"mapping": "ANTHROPIC_API_KEY", "runbook": "ANTHROPIC_API_KEY", "proposal": "ANTHROPIC_API_KEY",
        "severity": "TYPESAFE_API_KEY", "run_risk": "TYPESAFE_API_KEY",
        "run_start": "TYPESAFE_API_KEY"}  # a kind runs only when its key is set
PENDING_S, FINISHED_S = 2.0, 30.0  # how often the api's runs are polled
PENDING_DOWN_S, RUNS_DOWN_S = 5.0, 30.0  # the wait after a failed pending poll; after a 404 or a failed finished poll


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


def config_job(d: dict, mappings: dict, rules: dict) -> tuple | None:
    """events.md §6 record: keeps `mappings` and `rules` (id -> body) current; a runbook draft not yet converted -> a
    job."""
    kind, rid, body = d.get("kind"), d.get("id"), d.get("body")
    if type(rid) is not str:
        return None
    if kind in ("mapping", "rule"):
        copy = mappings if kind == "mapping" else rules
        if d.get("deleted") is True:
            copy.pop(rid, None)
        elif type(body) is dict:
            copy[rid] = body
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
        self.rules: dict[str, dict] = {}
        self.kinds = list(HANDLERS)  # the kinds `run_one` claims; main() keeps those whose key is set
        self.runs_due = {"pending": 0.0, "finished": 0.0}  # monotonic time of the next poll of the api's runs
        self.runs_down: set[str] = set()
        self.runs_cursor = None  # newest `finished_at` seen; None reads it from `run_history`

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
                    self._enqueue(config_job(d, self.mappings, self.rules))
        log.info("read %s to offset %d: %d mappings, %d rules", CONFIG_TOPIC, high, len(self.mappings), len(self.rules))

    def _change(self, d: dict) -> None:
        """events.md §4 record: its history row and a severity job if it triggers one, then a proposal job."""
        try:
            self._enqueue(judge.record(self.pg, d))
            self._enqueue(alert_job(d))
        except (psycopg.DataError, UnicodeEncodeError) as e:  # pg is autocommit: nothing to roll back
            log.warning("skipped %s record %s: Postgres cannot store it (%s)", CHANGES_TOPIC, d.get("change_id"), e)

    def poll_runs(self) -> None:
        """Poll the api's runs when due: `run_risk` jobs for pending checks, the finished runs into `run_history`.
        Any failure (no `/runs` yet, api down, junk) is logged once per outage and retried after PENDING_DOWN_S
        (pending) or RUNS_DOWN_S (finished, or a 404: the api has no `/runs`); it never stops the loop."""
        for name, every, step in (("pending", PENDING_S, lambda: runs.pending_jobs(self.api)),
                                  ("finished", FINISHED_S, self._sync_finished)):
            if time.monotonic() < self.runs_due[name]:
                continue
            try:
                found = step()
            except Exception as e:  # a trust boundary: whatever the api does, consumption goes on
                wait = RUNS_DOWN_S if name == "finished" or isinstance(e, ApiError) and e.status == 404 \
                    else PENDING_DOWN_S
                if name not in self.runs_down:
                    log.warning("polling %s runs failed (%s); retrying every %ds", name, e, wait)
                self.runs_down.add(name)
                self.runs_due[name] = time.monotonic() + wait
                continue
            if name in self.runs_down:
                log.info("polling %s runs works again", name)
                self.runs_down.discard(name)
            self.runs_due[name] = time.monotonic() + every
            for job in found:
                try:
                    self._enqueue(job)
                except (psycopg.DataError, UnicodeEncodeError) as e:
                    log.warning("skipped %s: Postgres cannot store it (%s)", job[0], e)

    def _sync_finished(self) -> list[tuple]:
        found, self.runs_cursor = runs.sync_finished(self.pg, self.api, self.runs_cursor, datetime.now(UTC))
        return found

    def poll(self, timeout: float) -> None:
        for consumer, translate, wait in ((self.samples, sample_job, timeout), (self.changes, self._change, 0),
                                          (self.config, lambda d: config_job(d, self.mappings, self.rules), 0)):
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
        job = jobs.claim(self.jobs_pg, self.kinds)
        if job is None:
            return IDLE_S
        log.info("running %s (attempt %d)", job["id"], job["attempts"] + 1)
        ctx = SimpleNamespace(api=self.api, mappings=list(self.mappings.values()),
                              other_samples=lambda: jobs.other_samples(self.jobs_pg, job["id"]),
                              run_agent=functools.partial(agent.run_agent, thread_id=job["id"]),
                              rules=self.rules, ask_jev=judge.ask_jev,
                              rule_stats=lambda rid: judge.stats(self.jobs_pg, rid, datetime.now(UTC)),
                              run_stats=lambda rid, rb: runs.stats(self.jobs_pg, rid, rb, datetime.now(UTC)),
                              now=lambda: datetime.now(UTC))
        wait = 0
        try:
            result = HANDLERS[job["kind"]](job["input"], ctx)
        except (JobFailed, ApiError) as e:
            log.warning("%s failed: %s", job["id"], e)
            status = jobs.finish(self.jobs_pg, job, error=str(e))
        except Exception as e:
            log.exception("%s failed, will retry", job["id"])
            status, wait = jobs.finish(self.jobs_pg, job, error=repr(e), retry=True), RETRY_S
        else:
            log.info("%s done", job["id"])
            status = jobs.finish(self.jobs_pg, job, result=result)
        if status != "pending" and KEYS[job["kind"]] == "ANTHROPIC_API_KEY":
            agent.forget(job["id"])  # a pending job keeps its checkpoints: the retry resumes from them
        return wait


def main() -> None:
    logging.basicConfig(level=os.environ.get("LOG_LEVEL", "INFO").upper(), stream=sys.stderr,
                        format="%(asctime)s %(levelname)s %(name)s %(message)s")
    if missing := [k for k in ("KAFKA_BROKERS", "DATABASE_URL", "API_URL") if not os.environ.get(k)]:
        sys.exit(f"ai-worker: missing env var {', '.join(missing)}")
    brokers, db_url = os.environ["KAFKA_BROKERS"], os.environ["DATABASE_URL"]

    with psycopg.connect(db_url, autocommit=True, row_factory=dict_row) as c:  # the lock is released on close
        c.execute("SELECT pg_advisory_lock(%s)", (MIGRATION_LOCK,))
        for f in sorted(MIGRATIONS.glob("*.sql")):
            c.execute(f.read_text())
        PostgresSaver(c).setup()  # LangGraph's checkpoint tables and their own migrations

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
    worker.kinds = [k for k in HANDLERS if os.environ.get(KEYS[k])]
    for key in sorted(set(KEYS.values()) - {KEYS[k] for k in worker.kinds}):
        log.warning("%s is not set: %s jobs are enqueued but not run", key,
                    ", ".join(k for k in HANDLERS if KEYS[k] == key))
    log.info("ai-worker started")
    wait = 0.0
    while not stop:
        worker.poll(wait)
        if "run_risk" in worker.kinds:
            worker.poll_runs()
        wait = worker.run_one()
    for c in (samples, changes, config):
        c.close()
    log.info("ai-worker stopped")
