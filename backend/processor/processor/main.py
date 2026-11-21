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
from confluent_kafka import OFFSET_BEGINNING, Consumer, KafkaError, KafkaException, Producer, TopicPartition

from .config import Config
from .core import TS_MAX, TS_MIN, build_envelope, match_rules
from .relay import MESSAGE_TIMEOUT_MS, Relay
from .store import TOPIC as CHANGES_TOPIC
from .store import DIRTY, REPLAY, Item, Store

log = logging.getLogger("processor")
RAW_TOPIC, CONFIG_TOPIC = "events.raw", "config.changes"
USER_CHANGES, OWN_ACTORS = ("acked", "resolved"), ("processor", "auto")
MIGRATIONS = Path(__file__).parent.parent / "migrations"
MIGRATION_LOCK = 0x70726F63  # any constant shared by all replicas
BATCH = 500
DRAIN_ROUNDS = 10  # full relay batches per step: a burst of change records drains fast, events still wait at most this many
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


def _user_change(msg, d: dict) -> bool:
    """events.md §4: is this an `acked` / `resolved` a person made through the api? Logs one that cannot be applied."""
    if d.get("change") not in USER_CHANGES or d.get("actor") in OWN_ACTORS:
        return False  # our own records and every other change: nothing to apply
    actor, alert = d.get("actor"), d.get("alert")
    if (type(actor) is str and actor and type(alert) is dict and type(alert.get("id")) is str
            and not _BAD.search(actor + alert["id"])):
        return True
    log.warning("skipped %s[%s]@%s: %s without a usable actor or alert.id", msg.topic(), msg.partition(), msg.offset(), d["change"])
    return False


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

    def __init__(self, consumer, config_consumer, changes_consumer, config, store, relay, now=time.monotonic,
                 utcnow=lambda: datetime.now(UTC)):
        self.consumer, self.config_consumer, self.changes_consumer, self.config, self.store, self.relay = (
            consumer, config_consumer, changes_consumer, config, store, relay)
        self.now, self.utcnow = now, utcnow
        self._flushed = self._resolved = self._refreshed = self._logged = now()
        self.events = self.matches = self._logged_events = 0
        self._replay = False  # parked events wait for a replay: set when the compiled config changed

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
            its = [Item(env, m, rules[m.rule_id].window_s, rules[m.rule_id].runbook_id, msg.partition(), msg.offset())
                   for m in match_rules(env, compiled)]
            items += its
            park = env, [it.match.rule_id for it in its]  # save_samples skips it when mapped
            if not env["mapped"]:
                unmapped.append(park)
            records.append((msg, park, its))
        try:
            self.store.save_samples(unmapped)
            self.store.process(items)
        except DATA_ERRORS:
            log.exception("the store rejected a value in this batch; applying it one record at a time")
            for msg, park, its in records:
                try:
                    self.store.save_samples([park])
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
        if msgs := self.changes_consumer.consume(num_messages=BATCH, timeout=0):
            if records := [d for m in msgs if (d := _decode(m)) and _user_change(m, d)]:
                self.store.apply_changes(records)
            if offsets := {(m.topic(), m.partition()): m.offset() + 1 for m in msgs if not m.error()}:
                _commit(self.changes_consumer, offsets)  # after apply: a crash before this line means redelivery
        for _ in range(DRAIN_ROUNDS):  # after the flush, auto-resolve and resolves, so their changes leave in this step too
            if self.relay.drain(BATCH) < BATCH:
                break
        if msgs := self.config_consumer.consume(num_messages=BATCH, timeout=0):
            if records := [d for m in msgs if (d := _decode(m))]:
                self.config.apply(records)
            if offsets := {(m.topic(), m.partition()): m.offset() + 1 for m in msgs if not m.error()}:
                _commit(self.config_consumer, offsets)  # after apply: a crash before this line means redelivery
        if t - self._refreshed >= REFRESH_S:
            if self.config.refresh():
                self._replay = True
            self._refreshed = t
        if self._replay:
            self._replay = not self.store.replay_parked(self._replay_rows)  # lock held elsewhere: retry next step
        if t - self._logged >= LOG_S:
            log.info("events=%d matches=%d events_per_s=%.1f",
                     self.events, self.matches, (self.events - self._logged_events) / (t - self._logged))
            self._logged, self._logged_events = t, self.events

    def _replay_rows(self, rows) -> list[int]:
        """Parked rows that map now -> their rule matches (minus the rules counted at arrival) into the store; returns
        the seqs of those rows. Rows that still do not map stay parked."""
        compiled = self.config.rules
        rules = {r.id: r.rule for r in compiled}
        items, done = [], []
        for seq, event, matched in rows:
            if not (env := build_envelope(event, self.config.mappings))["mapped"]:
                continue
            done.append(seq)
            items += [Item(env, m, rules[m.rule_id].window_s, rules[m.rule_id].runbook_id, REPLAY, seq)
                      for m in match_rules(env, compiled) if m.rule_id not in matched]
        try:
            self.store.process(items)
        except DATA_ERRORS:  # no record-by-record retry: replayed items skip the watermark, so a retry would count twice
            log.exception("the store rejected a replayed parked event; dropping %d parked events", len(done))
        return done


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


def _rewind_if_wiped(pg):
    """on_assign for `config.changes`: with no mappings and no rules the database was wiped (or is new), so replay the
    topic from the start; the committed offset would skip what the copy lost. Upserts are idempotent by updated_at."""
    # shortcut: a crash between the first replayed batch's apply and its commit leaves the tables partly filled and the offset at the end; wipe again to retry
    def on_assign(consumer, partitions):
        if not pg.execute("SELECT EXISTS (SELECT 1 FROM mappings) OR EXISTS (SELECT 1 FROM rules)").fetchone()[0]:
            log.info("no mappings and no rules: reading %s from the beginning", CONFIG_TOPIC)
            for p in partitions:
                p.offset = OFFSET_BEGINNING
        consumer.assign(partitions)
    return on_assign


def _run(brokers: str, db_url: str, redis_url: str) -> None:
    """One replica's loop until SIGTERM or SIGINT."""
    r = redis.Redis.from_url(redis_url, decode_responses=True)
    consumer = Consumer({"bootstrap.servers": brokers, "group.id": "processor",
                         "enable.auto.commit": False, "auto.offset.reset": "earliest"})
    consumer.subscribe([RAW_TOPIC])
    config_pg = psycopg.connect(db_url, autocommit=True)
    # one group shared by all replicas: every record reaches the shared tables once, a fresh group replays the topic
    config_consumer = Consumer({"bootstrap.servers": brokers, "group.id": "processor-config",
                                "enable.auto.commit": False, "auto.offset.reset": "earliest"})
    # config.changes has one partition, so one replica refills the tables and the others read them on refresh
    config_consumer.subscribe([CONFIG_TOPIC], on_assign=_rewind_if_wiped(config_pg))
    # the same for people's acks and resolves: each record is applied by one replica; a new group starts at the end,
    # an ack older than the group concerns an alert long since changed, and replaying the topic delayed fresh acks by minutes
    changes_consumer = Consumer({"bootstrap.servers": brokers, "group.id": "processor-changes",
                                 "enable.auto.commit": False, "auto.offset.reset": "latest"})
    changes_consumer.subscribe([CHANGES_TOPIC])
    # Relay needs per-key order on retries, and a final callback for every message within its flush
    producer = Producer({"bootstrap.servers": brokers, "enable.idempotence": True, "message.timeout.ms": MESSAGE_TIMEOUT_MS})
    store = Store(psycopg.connect(db_url, autocommit=True), r)
    relay = Relay(psycopg.connect(db_url, autocommit=True), producer, r)
    proc = Processor(consumer, config_consumer, changes_consumer, Config(config_pg), store, relay)
    proc._replay = True  # a crash may have parked events after the last replay

    stop = []
    for s in (signal.SIGTERM, signal.SIGINT):
        signal.signal(s, lambda *_: stop.append(True))
    log.info("processor started")
    while not stop:
        proc.step()
    store.flush(r.zrange(DIRTY, 0, -1))  # targeted, not the timed tick: a replica stopping at the same time holds the tick's lock
    relay.drain()
    consumer.close()
    config_consumer.close()
    changes_consumer.close()
    log.info("processor stopped")
