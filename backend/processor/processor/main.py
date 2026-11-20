"""Processor main loop: `events.raw` -> envelope -> rules -> Store -> outbox relay (OVERVIEW "How one event flows", steps 2-6)."""

import json
import logging
import multiprocessing
import multiprocessing.connection
import os
import re
import signal
import sys
import time
from datetime import UTC, datetime
from pathlib import Path

import psycopg
import redis
from confluent_kafka import Consumer, KafkaError, KafkaException, Producer, TopicPartition

from .config import Config
from .core import TS_MAX, TS_MIN, build_envelope, match_rules
from .relay import MESSAGE_TIMEOUT_MS, Relay
from .store import Item, Store

log = logging.getLogger("processor")
RAW_TOPIC, CONFIG_TOPIC = "events.raw", "config.changes"
MIGRATIONS = Path(__file__).parent.parent / "migrations"
MIGRATION_LOCK = 0x70726F63  # any constant shared by all replicas
BATCH = 500
FLUSH_S, RESOLVE_S, REFRESH_S, LOG_S = 1.0, 5.0, 2.0, 10.0
MAX_DEPTH = 64  # payload nesting; Postgres jsonb fails far deeper, json.loads recurses
_BAD = re.compile("[\x00\ud800-\udfff]")  # NUL (Postgres refuses it) and lone surrogates (UTF-8 cannot encode them)
# the store dedups a redelivered or replayed record, so a batch that hit one of these is applied again record by record
DATA_ERRORS = (psycopg.DataError, psycopg.errors.ProgramLimitExceeded, psycopg.errors.StatementTooComplex,
               UnicodeError, RecursionError)
_REBALANCE = (KafkaError.REBALANCE_IN_PROGRESS, KafkaError.ILLEGAL_GENERATION, KafkaError.UNKNOWN_MEMBER_ID)


def _finite(s: str) -> float | None:
    return f if (f := float(s)) - f == 0 else None  # 1e999 parses to inf, which jsonb refuses like NaN


def _decode(msg) -> dict | None:
    """A Kafka message -> its JSON object, or None (logged) for an error, non-JSON or non-object value."""
    if msg.error():
        log.warning("skipped %s: kafka error %s", msg.topic(), msg.error())
        return None
    try:
        doc = json.loads(msg.value(), parse_constant=lambda _: None, parse_float=_finite)
    except (TypeError, ValueError, RecursionError):
        doc = None
    if type(doc) is not dict:
        log.warning("skipped %s[%s]@%s: not a JSON object", msg.topic(), msg.partition(), msg.offset())
        return None
    return doc


def _raw_ok(e: dict) -> bool:
    """events.md §2 by hand: a pydantic model per event costs too much on the hot path."""
    try:
        t = datetime.fromisoformat(e["received_at"])
        ok = t.tzinfo is not None and TS_MIN <= t.astimezone(UTC) <= TS_MAX  # the store does tz-aware arithmetic on it
    except (KeyError, TypeError, ValueError, OverflowError):
        return False
    return ok and type(e.get("event_id")) is str and type(e.get("source_id")) is str and "payload" in e


def _suspect(value: bytes) -> bool:
    """From the raw bytes: could the event hold a NUL, a surrogate or deep nesting? Almost always False."""
    return (b"\\u0000" in value or b"\\ud" in value or b"\\uD" in value
            or value.count(b"{") + value.count(b"[") > MAX_DEPTH)


def _clean(v, depth: int = 1):
    """Copy with NUL and lone surrogates in strings and keys replaced by U+FFFD; ValueError past MAX_DEPTH."""
    if type(v) is str:
        return _BAD.sub("\ufffd", v)
    if type(v) is dict or type(v) is list:
        if depth > MAX_DEPTH:
            raise ValueError("too deep")
        if type(v) is list:
            return [_clean(x, depth + 1) for x in v]
        return {_BAD.sub("\ufffd", k): _clean(x, depth + 1) for k, x in v.items()}
    return v


def _repaired(e: dict) -> dict | None:
    """The event with its payload cleaned, or None when an id holds a NUL or surrogate or the payload is too deep."""
    if _BAD.search(e["event_id"]) or _BAD.search(e["source_id"]):
        return None
    try:
        return {**e, "payload": _clean(e["payload"])}
    except ValueError:
        return None


def _commit(consumer, offsets: dict) -> None:
    try:
        consumer.commit(offsets=[TopicPartition(t, p, o) for (t, p), o in offsets.items()], asynchronous=False)
    except KafkaException as e:
        if not (e.args and isinstance(e.args[0], KafkaError) and e.args[0].code() in _REBALANCE):
            raise
        log.warning("commit skipped during a group rebalance (%s); the records are redelivered", e.args[0].name())


class Processor:
    """One replica's loop. `now` is a monotonic clock in seconds, read only here, for the housekeeping intervals;
    `utcnow` is the wall clock (aware UTC datetime) that auto-resolve compares ingest times with."""

    def __init__(self, consumer, config_consumer, config, store, relay, now=time.monotonic,
                 utcnow=lambda: datetime.now(UTC)):
        self.consumer, self.config_consumer, self.config, self.store, self.relay, self.now, self.utcnow = (
            consumer, config_consumer, config, store, relay, now, utcnow)
        self._flushed = self._resolved = self._refreshed = self._logged = now()
        self.events = self.matches = self._logged_events = 0

    def step(self, timeout: float = 0.1) -> None:
        # shortcut: Postgres, Redis and Kafka commit errors end the process, the container restarts it and Kafka redelivers
        msgs = self.consumer.consume(num_messages=BATCH, timeout=timeout)
        compiled = self.config.rules
        rules = {r.id: r.rule for r in compiled}
        items, unmapped, offsets, records = [], [], {}, []
        for msg in msgs:
            if not msg.error():  # an error message has no offset of its own
                offsets[msg.topic(), msg.partition()] = msg.offset() + 1  # bad records are committed too, never retried
            if (event := _decode(msg)) is None:
                continue
            if not _raw_ok(event):
                log.warning("skipped %s[%s]@%s: not a raw event", msg.topic(), msg.partition(), msg.offset())
                continue
            if _suspect(msg.value()) and (event := _repaired(event)) is None:
                log.warning("skipped %s[%s]@%s: NUL or lone surrogate in event_id or source_id, or payload nested"
                            " deeper than %d", msg.topic(), msg.partition(), msg.offset(), MAX_DEPTH)
                continue
            self.events += 1
            env = build_envelope(event, self.config.mappings)
            if not env["mapped"]:
                unmapped.append(env)
            its = [Item(env, m, rules[m.rule_id].window_s, rules[m.rule_id].runbook_id, msg.partition(), msg.offset())
                   for m in match_rules(env, compiled)]
            items += its
            records.append((msg, env, its))
        try:
            self.store.save_samples(unmapped)
            self.store.process(items)
        except DATA_ERRORS:
            log.exception("the store rejected a value in this batch; applying it one record at a time")
            for msg, env, its in records:
                try:
                    self.store.save_samples([env])
                    self.store.process(its)
                except DATA_ERRORS as e:
                    log.warning("skipped %s[%s]@%s: the store rejected it (%r)", msg.topic(), msg.partition(), msg.offset(), e)
        if offsets:  # only after the store returned: a crash before this line means redelivery, which the store dedups
            _commit(self.consumer, offsets)
        self.matches += len(items)

        t = self.now()
        if t - self._flushed >= FLUSH_S:
            self.store.flush()
            self._flushed = t
        if t - self._resolved >= RESOLVE_S:
            self.store.resolve_quiet(self.utcnow())
            self._resolved = t
        self.relay.drain()  # after the flush and auto-resolve, so their changes leave in this step too
        if msgs := self.config_consumer.consume(num_messages=BATCH, timeout=0):
            if records := [d for m in msgs if (d := _decode(m))]:
                self.config.apply(records)
            if offsets := {(m.topic(), m.partition()): m.offset() + 1 for m in msgs if not m.error()}:
                _commit(self.config_consumer, offsets)  # after apply: a crash before this line means redelivery
        if t - self._refreshed >= REFRESH_S:
            self.config.refresh()
            self._refreshed = t
        if t - self._logged >= LOG_S:
            log.info("events=%d matches=%d events_per_s=%.1f",
                     self.events, self.matches, (self.events - self._logged_events) / (t - self._logged))
            self._logged, self._logged_events = t, self.events


def _logging() -> None:
    logging.basicConfig(level=os.environ.get("LOG_LEVEL", "INFO").upper(), stream=sys.stderr,
                        format="%(asctime)s %(levelname)s %(name)s %(message)s")


def main() -> None:
    _logging()
    if missing := [k for k in ("KAFKA_BROKERS", "DATABASE_URL", "REDIS_URL") if not os.environ.get(k)]:
        sys.exit(f"processor: missing env var {', '.join(missing)}")
    raw_workers = os.environ.get("PROCESSOR_WORKERS", "1")
    workers = int(raw_workers) if re.fullmatch(r"[0-9]+", raw_workers) else 0
    if workers < 1:
        sys.exit(f"processor: PROCESSOR_WORKERS must be a positive integer, got {raw_workers!r}")
    args = os.environ["KAFKA_BROKERS"], os.environ["DATABASE_URL"], os.environ["REDIS_URL"]

    with psycopg.connect(args[1], autocommit=True) as c:  # the lock is released when the connection closes
        c.execute("SELECT pg_advisory_lock(%s)", (MIGRATION_LOCK,))
        for f in sorted(MIGRATIONS.glob("*.sql")):
            c.execute(f.read_text())
    if workers == 1:
        _run(*args)
    else:
        _supervise(workers, args)


def _worker(*args) -> None:
    _logging()  # a spawned process starts with nothing configured
    _run(*args)


def _supervise(n: int, args: tuple) -> None:
    """Run n spawned workers, each a full replica; stop all on SIGTERM/SIGINT or when any one exits on its own."""
    ctx = multiprocessing.get_context("spawn")  # nothing inherited: each worker opens its own connections
    procs = [ctx.Process(target=_worker, args=args, name=f"processor-{i}") for i in range(n)]
    stop = []
    for s in (signal.SIGTERM, signal.SIGINT):
        signal.signal(s, lambda *_: stop.append(True))
    for p in procs:
        p.start()
    log.info("started %d workers", n)
    while not stop and all(p.is_alive() for p in procs):
        multiprocessing.connection.wait([p.sentinel for p in procs], timeout=0.5)
    if not stop:
        log.error("a worker exited on its own (%s); stopping the others",
                  ", ".join(f"{p.name}={p.exitcode}" for p in procs if not p.is_alive()))
    for p in procs:
        if p.is_alive():
            p.terminate()  # SIGTERM: the worker finishes its step and shuts down like a single process
    for p in procs:
        p.join()
    codes = [p.exitcode for p in procs]
    log.info("workers stopped: %s", codes)
    sys.exit(0 if stop and codes == [0] * n else 1)


def _run(brokers: str, db_url: str, redis_url: str) -> None:
    """One replica's loop until SIGTERM or SIGINT."""
    r = redis.Redis.from_url(redis_url, decode_responses=True)
    consumer = Consumer({"bootstrap.servers": brokers, "group.id": "processor",
                         "enable.auto.commit": False, "auto.offset.reset": "earliest"})
    consumer.subscribe([RAW_TOPIC])
    # one group shared by all replicas: every record reaches the shared tables once, a fresh group replays the topic
    config_consumer = Consumer({"bootstrap.servers": brokers, "group.id": "processor-config",
                                "enable.auto.commit": False, "auto.offset.reset": "earliest"})
    config_consumer.subscribe([CONFIG_TOPIC])
    # Relay needs per-key order on retries, and a final callback for every message within its flush
    producer = Producer({"bootstrap.servers": brokers, "enable.idempotence": True, "message.timeout.ms": MESSAGE_TIMEOUT_MS})
    store = Store(psycopg.connect(db_url, autocommit=True), r)
    relay = Relay(psycopg.connect(db_url, autocommit=True), producer, r)
    proc = Processor(consumer, config_consumer, Config(psycopg.connect(db_url, autocommit=True)), store, relay)

    stop = []
    for s in (signal.SIGTERM, signal.SIGINT):
        signal.signal(s, lambda *_: stop.append(True))
    log.info("processor started")
    while not stop:
        proc.step()
    store.flush()
    relay.drain()
    consumer.close()
    config_consumer.close()
    log.info("processor stopped")
