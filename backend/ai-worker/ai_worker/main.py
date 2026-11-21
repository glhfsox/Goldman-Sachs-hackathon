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

import anthropic
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
IDLE_S = 1.0  # poll wait with no job to run (a failed job's own backoff is `jobs.run_after`)
DOWN_S = 5.0  # wait after a database or Kafka error before the loop goes on (reconnecting to the database)
DB_ERRORS = (psycopg.OperationalError, psycopg.InterfaceError)  # our own database: reconnect, never fail the job
MAX_POLL_INTERVAL_MS = 1_800_000  # a job (up to 6 model calls) runs between polls; a rebalance past it only redelivers

HANDLERS = {"mapping": mapping.handle, "runbook": runbooks.convert, "proposal": runbooks.propose,
            "severity": judge.handle, "run_risk": runs.risk, "run_start": runs.start}
KEYS = {"mapping": "ANTHROPIC_API_KEY", "runbook": "ANTHROPIC_API_KEY", "proposal": "ANTHROPIC_API_KEY",
        "severity": "TYPESAFE_API_KEY", "run_risk": "TYPESAFE_API_KEY",
        "run_start": "TYPESAFE_API_KEY"}  # a kind runs only when its key is set
PENDING_S, FINISHED_S = 2.0, 30.0  # how often the api's runs are polled
PENDING_DOWN_S, RUNS_DOWN_S = 5.0, 30.0  # the wait after a failed pending poll; after a 404 or a failed finished poll


def _db_down(e: Exception) -> bool:
    """Lost the database itself (no SQLSTATE, class 08 connection exception, 57P shutdown / terminated backend):
    reconnect. Any other OperationalError (a limit, a deadlock, a cancelled query) is the job's own error."""
    state = getattr(e, "sqlstate", None)
    return isinstance(e, psycopg.InterfaceError) or not state or state.startswith(("08", "57P"))


def _permanent(e: Exception) -> bool:
    """Retrying changes nothing: JobFailed (a refusal, a rejected submission, a Jev 4xx), an api 4xx, or an Anthropic
    4xx other than 408, 409 and 429. Everything else (5xx, 529, timeouts, connection errors, junk answers) retries."""
    return isinstance(e, (JobFailed, ApiError)) or (isinstance(e, anthropic.APIStatusError)
                                                     and 400 <= e.status_code < 500 and e.status_code not in (408, 409, 429))


def write_metrics(line: dict) -> None:
    """Append one JSON line to AGENT_METRICS_FILE; a failure is logged and never breaks the job."""
    try:
        path = Path(os.environ.get("AGENT_METRICS_FILE") or "logs/agent-metrics.jsonl")
        path.parent.mkdir(parents=True, exist_ok=True)
        with open(path, "a") as f:
            f.write(json.dumps(line) + "\n")
    except Exception as e:
        log.warning("could not write agent metrics (%s)", e)


def _metrics_line(job: dict, outcome: str, err, retry_in, started: float, m: dict, result) -> dict:
    """No payloads, prompts, keys or user ids: counts, the model, the error's class name, the judge's answer."""
    line = {"ts": datetime.now(UTC).isoformat(timespec="milliseconds"), "job_id": job["id"], "kind": job["kind"],
            "attempt": job["attempts"] + 1, "outcome": outcome, "error_class": err and type(err).__name__,
            "retry_in_s": retry_in and round(retry_in, 1), "duration_ms": round((time.monotonic() - started) * 1000)}
    if "model_calls" in m:
        return line | m | {"model_calls_total": m["prior_model_calls"] + m["model_calls"],
                           "cost_usd": m["cost_usd"] and round(m["cost_usd"], 6)}
    r = result if type(result) is dict else {}
    a = r.get("answer") if type(r.get("answer")) is dict else {}
    return line | m | {"choice": a.get("choice"), "confidence": a.get("confidence"), "verdict": r.get("verdict"),
                       "posted": bool(r.get("proposal_id") or r.get("posted"))}


def _timed(m: dict, ask):
    def call(state, questions):
        t = time.monotonic()
        m["jev_calls"] += 1
        try:
            return ask(state, questions)
        finally:
            m["jev_latency_ms"] += round((time.monotonic() - t) * 1000)
    return call


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

    def reconnect(self, db_url: str) -> None:
        """New database connections after an error. Closing the old claim connection rolls back its transaction, so
        a job claimed on it is `pending` again with its attempts unchanged."""
        for c in (self.pg, self.jobs_pg):
            c.close()
        agent.reset()
        self.pg, self.jobs_pg = psycopg.connect(db_url, autocommit=True), psycopg.connect(db_url)

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
            try:
                for msg in msgs:
                    if d := _decode(msg):
                        self._enqueue(translate(d))
            except Exception:  # rewind: the next consume reads the batch again instead of skipping past it
                first = {}
                for msg in msgs:
                    if not msg.error():
                        first.setdefault((msg.topic(), msg.partition()), msg.offset())
                for (topic, partition), offset in first.items():
                    consumer.seek(TopicPartition(topic, partition, offset))
                raise
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
        claude = KEYS[job["kind"]] == "ANTHROPIC_API_KEY"
        m, started = agent.new_metrics() if claude else {"jev_calls": 0, "jev_latency_ms": 0}, time.monotonic()
        ctx = SimpleNamespace(api=self.api, mappings=list(self.mappings.values()),
                              other_samples=lambda: jobs.other_samples(self.jobs_pg, job["id"]),
                              run_agent=functools.partial(agent.run_agent, thread_id=job["id"], metrics=m),
                              rules=self.rules, ask_jev=_timed(m, judge.ask_jev),
                              rule_stats=lambda rid: judge.stats(self.jobs_pg, rid, datetime.now(UTC)),
                              run_stats=lambda rid, rb: runs.stats(self.jobs_pg, rid, rb, datetime.now(UTC)),
                              now=lambda: datetime.now(UTC))
        outcome, err, retry_in, result = "retry", None, None, None
        try:
            try:
                result = HANDLERS[job["kind"]](job["input"], ctx)
            except DB_ERRORS as e:
                if _db_down(e):
                    raise
                err = e  # a statement that fails the same way every time: count an attempt, don't reconnect forever
                retry_in = jobs.backoff(job["kind"], job["attempts"] + 1)
                log.exception("%s failed, retry in %.0f s", job["id"], retry_in)
                status = jobs.finish(self.jobs_pg, job, error=repr(e), retry_in=retry_in)
            except Exception as e:
                err = e
                if _permanent(e):
                    log.warning("%s failed: %s", job["id"], e)
                    status = jobs.finish(self.jobs_pg, job, error=str(e))
                else:
                    retry_in = jobs.backoff(job["kind"], job["attempts"] + 1)
                    log.exception("%s failed, retry in %.0f s", job["id"], retry_in)
                    status = jobs.finish(self.jobs_pg, job, error=repr(e), retry_in=retry_in)
            else:
                log.info("%s done", job["id"])
                status = jobs.finish(self.jobs_pg, job, result=result)
            outcome = "retry" if status == "pending" else status
            retry_in = retry_in if status == "pending" else None
        except DB_ERRORS as e:  # the database, not the job: main() reconnects, the row stays pending, no attempt counted
            err, retry_in = e, None
            raise
        finally:
            write_metrics(_metrics_line(job, outcome, err, retry_in, started, m, result))
        return 0


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
    wait, down = 0.0, False
    while not stop:
        try:
            if down:
                worker.reconnect(db_url)
                log.info("database reachable again")
                down = False
            worker.poll(wait)
            if "run_risk" in worker.kinds:
                worker.poll_runs()
            wait = worker.run_one()
        except DB_ERRORS as e:
            if not down:
                log.warning("database error (%s); reconnecting every %.0f s", e, DOWN_S)
            down, wait = True, 0.0
            time.sleep(DOWN_S)
        except KafkaException as e:
            log.warning("kafka error (%s); retrying in %.0f s", e, DOWN_S)
            wait = 0.0
            time.sleep(DOWN_S)
    for c in (samples, changes, config):
        c.close()
    log.info("ai-worker stopped")
