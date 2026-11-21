import json
import logging
from datetime import UTC, datetime, timedelta

import psycopg
import pytest

from conftest import EXAMPLE_RULES, PAYLOADS, SEED_MAPPINGS, make_event
from processor import compile_mappings, compile_rules
from processor.config import Config
from processor.main import Processor
from processor.relay import Relay
from processor.store import Store


class Msg:
    def __init__(self, value, offset, partition=0, topic="events.raw", error=None):
        self._value = value if value is None or type(value) is bytes else json.dumps(value).encode()
        self._offset, self._partition, self._topic, self._error = offset, partition, topic, error

    def value(self): return self._value
    def offset(self): return self._offset
    def partition(self): return self._partition
    def topic(self): return self._topic
    def error(self): return self._error


class FakeConsumer:
    """`fail`: exceptions the next commits raise, one per commit, before anything is committed."""
    def __init__(self, calls):
        self.queue, self.commits, self.calls, self.fail = [], [], calls, []

    def consume(self, num_messages, timeout):
        out, self.queue = self.queue[:num_messages], self.queue[num_messages:]
        return out

    def commit(self, offsets, asynchronous):
        assert asynchronous is False
        self.calls.append("commit")
        if self.fail:
            raise self.fail.pop(0)
        self.commits.append(sorted((tp.topic, tp.partition, tp.offset) for tp in offsets))


class FakeConfig:
    def __init__(self, mappings, rules, calls):
        self.mappings, self.rules, self.calls, self.applied = mappings, rules, calls, []
        self.changed = False  # what the next refresh returns

    def apply(self, records):
        self.calls.append("config.apply")
        self.applied.append(records)

    def refresh(self):
        self.calls.append("config.refresh")
        changed, self.changed = self.changed, False
        return changed


class FakeRelay:
    """`full`: how many drains in a row return a full batch."""
    def __init__(self, calls):
        self.calls, self.full = calls, 0

    def drain(self, limit=500):
        self.calls.append("relay.drain")
        self.full -= 1
        return limit if self.full >= 0 else 0


T0 = datetime(2026, 11, 20, 10, 15, 2, 123000, tzinfo=UTC)  # received_at of make_event


class Spy:
    """Records each method call on the wrapped object by name, then forwards it."""
    def __init__(self, obj, name, calls):
        self._obj, self._name, self._calls = obj, name, calls

    def __getattr__(self, attr):
        f = getattr(self._obj, attr)

        def call(*a, **k):
            self._calls.append(f"{self._name}.{attr}")
            return f(*a, **k)
        return call


@pytest.fixture
def conns(db):
    opened = []

    def make():
        opened.append(psycopg.connect(db, autocommit=True))
        return opened[-1]
    yield make
    for c in opened:
        c.close()


@pytest.fixture
def world(conns, sql, new_redis, mappings, rules):
    """A Processor on the real Store with fake Kafka, config and relay; `w.clock[0]` is the time `now` returns,
    `w.wall[0]` the one `utcnow` returns."""
    class W:
        pass
    w = W()
    w.calls, w.clock, w.wall = [], [0.0], [T0]
    w.consumer, w.config_consumer, w.changes_consumer = FakeConsumer(w.calls), FakeConsumer(w.calls), FakeConsumer(w.calls)
    w.config = FakeConfig(mappings, rules, w.calls)
    w.store = Store(conns(), new_redis())
    w.proc = Processor(w.consumer, w.config_consumer, w.changes_consumer, w.config, Spy(w.store, "store", w.calls),
                       FakeRelay(w.calls), now=lambda: w.clock[0], utcnow=lambda: w.wall[0])
    return w


def event(name, n):
    return make_event(*PAYLOADS[name], n)


def changes(sql, kind=None):
    rows = [r["payload"] for r in sql("SELECT payload FROM outbox WHERE topic = 'alerts.changes' ORDER BY id")]
    return [r for r in rows if kind is None or r["change"] == kind]


def test_batch_opens_alerts_commits_after_process_and_drains(world, sql):
    world.consumer.queue = [Msg(event(name, i), i) for i, name in enumerate(PAYLOADS)]
    world.proc.step()
    assert world.calls == ["store.save_samples", "store.process", "commit", "relay.drain"]
    assert world.consumer.commits == [[("events.raw", 0, 4)]]
    rule_ids = {"settlements-workflow-failed", "anna-payments-critical", "rates-large-rejections", "pricing-engine-errors"}
    assert {r["rule_id"] for r in sql("SELECT rule_id FROM alerts")} == rule_ids
    assert {c["alert"]["rule_id"] for c in changes(sql, "opened")} == rule_ids
    assert world.proc.events == 4 and world.proc.matches == 4


def test_burst_is_one_alert_counted_on_the_timed_flush(world, sql):
    for s in range(5):
        world.clock[0] = s * 0.2
        world.consumer.queue = [Msg(event("workflow", n), n) for n in range(s * 100, s * 100 + 100)]
        world.proc.step()
    assert "store.flush" not in world.calls
    assert sql("SELECT count FROM alerts") == [{"count": 1}]
    world.clock[0] = 1.0
    world.proc.step()
    assert sql("SELECT count FROM alerts") == [{"count": 500}]
    assert len(changes(sql, "opened")) == 1
    assert world.consumer.commits[-1] == [("events.raw", 0, 500)]


def test_bad_messages_are_skipped_and_committed(world, sql, caplog):
    no_payload = {k: v for k, v in event("trade", 3).items() if k != "payload"}
    no_received = {k: v for k, v in event("trade", 4).items() if k != "received_at"}
    naive_received = {**event("trade", 5), "received_at": "2026-11-20"}
    world.consumer.queue = [
        Msg(event("workflow", 0), 0),
        Msg(None, -1001, error="broker down"),
        Msg(b"not json {", 1),
        Msg([1, 2], 2),
        Msg(no_payload, 3),
        Msg(no_received, 4),
        Msg(naive_received, 5),
        Msg(event("trade", 6), 6),
        Msg(event("metric", 9), 9, partition=1),
    ]
    with caplog.at_level(logging.WARNING, logger="processor"):
        world.proc.step()
    assert len([r for r in caplog.records if r.name == "processor"]) == 6
    assert world.consumer.commits == [[("events.raw", 0, 7), ("events.raw", 1, 10)]]
    assert {r["rule_id"] for r in sql("SELECT rule_id FROM alerts")} == {
        "settlements-workflow-failed", "rates-large-rejections", "anna-payments-critical"}
    assert world.proc.events == 3


def test_unmapped_event_saves_a_sample_and_raw_rules_still_match(world, sql):
    [raw_rule], errors = compile_rules([{"id": "raw-catch", "team_id": "ops", "route": {"recipients": [{"team": "ops"}]},
                                         "when": {"all": [{"field": "raw.foo", "op": "eq", "value": "bar"}]}}])
    assert errors == []
    world.config.rules = [*world.config.rules, raw_rule]
    world.consumer.queue = [Msg(make_event("new-source", {"foo": "bar"}, 1), 0),
                            Msg(make_event("new-source", {"foo": "bar"}, 2), 1)]
    world.proc.step()
    [sample] = sql("SELECT key, payload FROM outbox WHERE topic = 'schema.samples'")
    assert sample["payload"]["source_id"] == "new-source" and sample["payload"]["sample"] == {"foo": "bar"}
    [alert] = sql("SELECT rule_id, sample_event FROM alerts")
    assert alert["rule_id"] == "raw-catch" and alert["sample_event"]["mapped"] is False


def test_parked_events_replay_when_a_mapping_arrives(world, sql):
    compiled, errors = compile_rules([
        {"id": "raw-catch", "team_id": "ops", "route": {"recipients": [{"team": "ops"}]},
         "when": {"all": [{"field": "raw.kind", "op": "eq", "value": "disk_full"}]}},
        {"id": "disk-typed", "team_id": "ops", "route": {"recipients": [{"team": "ops"}]},
         "when": {"all": [{"field": "type", "op": "eq", "value": "disk_full"}]}}])
    assert errors == []
    world.config.rules = [*world.config.rules, *compiled]
    disk = [make_event("vendor-x", {"kind": "disk_full", "host": "h1"}, n) for n in range(5)]
    other = [make_event("vendor-x", {"other": n}, n) for n in range(5, 7)]
    world.consumer.queue = [Msg(e, i) for i, e in enumerate(disk + other)]
    world.proc.step()
    assert len(sql("SELECT seq FROM parked_events")) == 7
    mapping = {"id": "vendor-x-disk", "status": "active", "origin": "llm", "priority": 100,
               "match": {"all": [{"field": "raw.kind", "op": "exists"}]}, "fields": {"type": {"path": "kind"}}}
    world.config.mappings, errors = compile_mappings([*SEED_MAPPINGS, mapping])
    assert errors == []
    world.config.changed, world.clock[0] = True, 2.0
    world.proc.step()  # refreshes, then replays
    world.clock[0] = 3.0
    world.proc.step()  # flushes the replayed counts
    assert [r["event"]["event_id"] for r in sql("SELECT event FROM parked_events ORDER BY seq")] == ["evt-5", "evt-6"]
    alerts = {a["rule_id"]: a for a in sql("SELECT rule_id, count, sample_event, opened_partition FROM alerts")}
    assert set(alerts) == {"raw-catch", "disk-typed"}
    assert alerts["raw-catch"]["count"] == 5  # counted at arrival, not again on replay
    typed = alerts["disk-typed"]
    assert (typed["count"], typed["sample_event"]["mapped"], typed["opened_partition"]) == (5, True, -1)
    assert [c["alert"]["rule_id"] for c in changes(sql, "opened")] == ["raw-catch", "disk-typed"]


def test_store_failure_propagates_and_nothing_is_committed(world, monkeypatch):
    def boom(items):
        raise psycopg.OperationalError("postgres gone")
    monkeypatch.setattr(world.store, "process", boom)
    world.consumer.queue = [Msg(event("workflow", 0), 0)]
    with pytest.raises(psycopg.OperationalError):
        world.proc.step()
    assert world.consumer.commits == [] and "relay.drain" not in world.calls


def test_housekeeping_follows_the_injected_clock(world, caplog):
    record = {"kind": "rule", "id": "r1", "updated_at": "2026-11-20T09:30:00.000Z", "deleted": False, "body": {"id": "r1"}}
    world.config_consumer.queue = [Msg(b"{bad", 0, topic="config.changes"), Msg(record, 1, topic="config.changes")]
    world.clock[0] = 0.9
    world.proc.step()
    assert world.config.applied == [[record]]
    assert "store.flush" not in world.calls and "config.refresh" not in world.calls
    world.clock[0] = 1.0
    world.proc.step()
    assert world.calls.count("store.flush") == 1 and "config.refresh" not in world.calls
    world.clock[0] = 2.0
    world.proc.step()
    assert world.calls.count("store.flush") == 2 and world.calls.count("config.refresh") == 1
    assert world.config.applied == [[record]]  # nothing new on the config topic: apply is not called
    world.consumer.queue = [Msg(event("workflow", n), n) for n in range(20)]
    world.clock[0] = 10.0
    caplog.clear()  # drops the warning about the bad config record
    with caplog.at_level(logging.INFO, logger="processor"):
        world.proc.step()
    [line] = [r.getMessage() for r in caplog.records if r.name == "processor"]
    assert line == "events=20 matches=20 events_per_s=2.0"


def test_auto_resolve_runs_every_5_s_with_the_wall_clock_before_the_drain(world, monkeypatch):
    seen = []
    monkeypatch.setattr(world.store, "resolve_quiet", lambda now: seen.append(now) or 0)
    world.clock[0] = 4.9
    world.proc.step()
    assert seen == []
    world.clock[0], world.wall[0] = 5.0, T0 + timedelta(seconds=999)
    world.proc.step()
    assert seen == [T0 + timedelta(seconds=999)]
    i = world.calls.index("store.resolve_quiet")
    assert world.calls[i + 1] == "relay.drain"
    world.clock[0] = 9.9
    world.proc.step()
    assert len(seen) == 1


def test_relay_drains_again_while_batches_are_full_up_to_a_bound(world):
    from processor.main import DRAIN_ROUNDS
    world.proc.relay.full = 2
    world.proc.step()
    assert world.calls.count("relay.drain") == 3
    world.calls.clear()
    world.proc.relay.full = 100
    world.proc.step()
    assert world.calls.count("relay.drain") == DRAIN_ROUNDS


def test_empty_batch_still_runs_housekeeping_and_drains(world):
    world.clock[0] = 2.0
    world.proc.step()
    assert world.calls == ["store.save_samples", "store.process", "store.flush", "relay.drain", "config.refresh"]
    assert world.consumer.commits == []


class FakeProducer:
    def __init__(self):
        self.sent = []

    def produce(self, topic, key, value, on_delivery):
        self.sent.append((topic, key, json.loads(value)))
        on_delivery(None, None)

    def flush(self, timeout=None):
        return 0


def test_end_to_end_with_real_config_and_relay(conns, sql, new_redis):
    with conns() as c:
        c.execute("TRUNCATE mappings, rules, service_deps")
    at = "2026-11-20T09:30:00.000Z"
    records = [{"kind": "mapping", "id": "workflow-failed", "updated_at": at, "deleted": False, "body": SEED_MAPPINGS[0]},
               {"kind": "rule", "id": EXAMPLE_RULES[0]["id"], "updated_at": at, "deleted": False, "body": EXAMPLE_RULES[0]}]
    calls, producer, r = [], FakeProducer(), new_redis()
    consumer, config_consumer = FakeConsumer(calls), FakeConsumer(calls)
    config_consumer.queue = [Msg(rec, i, topic="config.changes") for i, rec in enumerate(records)]
    clock, wall = [0.0], [T0]
    proc = Processor(consumer, config_consumer, FakeConsumer(calls), Config(conns()), Store(conns(), r), Relay(conns(), producer, r),
                     now=lambda: clock[0], utcnow=lambda: wall[0])
    proc.step()  # applies the records
    clock[0] = 2.0
    proc.step()  # refreshes: the processor now has the mapping and the rule
    consumer.queue = [Msg(event("workflow", 0), 0)]
    proc.step()
    [(topic, key, change)] = producer.sent
    assert (topic, change["change"], change["alert"]["rule_id"]) == ("alerts.changes", "opened", "settlements-workflow-failed")
    assert key == change["alert"]["id"] and sql("SELECT * FROM outbox") == []
    r.delete(*r.keys("proc:a:*"))  # window_s passed without an event: the hash expired
    clock[0], wall[0] = 5.0, T0 + timedelta(seconds=306)
    proc.step()
    [(topic, key, resolved)] = producer.sent[1:]
    assert (topic, key, resolved["change"], resolved["actor"]) == ("alerts.changes", change["alert"]["id"], "resolved", "auto")


# --- records on events.raw that must not stop the loop ---

def raw_with(n, step=None, **top):
    """A workflow event as JSON text; `step` is spliced in as raw JSON text, `top` overrides event fields."""
    e = {**event("workflow", n), **top}
    e["payload"]["error"]["code"] = f"C{n}"  # one alert per event
    text = json.dumps(e)
    return text.replace('"settle"', step).encode() if step is not None else text.encode()


def nested(depth):
    return "[" * depth + "]" * depth


REPAIRED = {  # raw JSON text of `step` -> what the alert keeps
    "nul": ('"a\\u0000b"', "a�b"),
    "lone surrogate": ('"\\ud800x"', "�x"),
    "surrogate pair kept": ('"\\ud83d\\ude00"', "\U0001f600"),
    "nul in a key": ('{"k\\u0000": "v"}', {"k�": "v"}),
    "NaN": ("NaN", None),
    "Infinity": ("Infinity", None),
    "-Infinity": ("-Infinity", None),
    "float overflow": ("1e999", None),
    "nesting 64 deep": (nested(63), json.loads(nested(63))),  # the payload object is level 1
}


@pytest.mark.parametrize("name", list(REPAIRED))
def test_odd_payload_values_are_repaired_not_dropped(world, sql, name):
    step, kept = REPAIRED[name]
    world.consumer.queue = [Msg(raw_with(0, step), 0), Msg(raw_with(1), 1)]
    world.proc.step()
    assert world.consumer.commits == [[("events.raw", 0, 2)]]
    alerts = {a["opened_by_event"]: a for a in sql("SELECT opened_by_event, fields, sample_event FROM alerts")}
    assert set(alerts) == {"evt-0", "evt-1"}
    assert alerts["evt-0"]["fields"]["step"] == kept
    assert alerts["evt-0"]["sample_event"]["raw"]["step"] == kept


SKIPPED = {
    "nul in event_id": dict(event_id="e\u0000"),
    "lone surrogate in event_id": dict(event_id="e\ud800"),
    "nul in source_id": dict(source_id="workflows\u0000"),
    "lone surrogate in source_id": dict(source_id="\udfff"),
    "received_at before year 1 in UTC": dict(received_at="0001-01-01T00:00:00+14:00"),
    "received_at after year 9999 in UTC": dict(received_at="9999-12-31T23:59:59-12:00"),
    "nesting 65 deep": dict(step=nested(64)),
    "nesting 20,000 deep": dict(step=nested(20_000)),
    "nesting 100,000 deep": dict(step=nested(100_000)),  # RecursionError inside json.loads
}


@pytest.mark.parametrize("name", list(SKIPPED))
def test_unrepresentable_records_are_skipped_logged_and_committed(world, sql, caplog, name):
    world.consumer.queue = [Msg(raw_with(0, **SKIPPED[name]), 0), Msg(raw_with(1), 1)]
    with caplog.at_level(logging.WARNING, logger="processor"):
        world.proc.step()
    assert world.consumer.commits == [[("events.raw", 0, 2)]]
    assert [a["opened_by_event"] for a in sql("SELECT opened_by_event FROM alerts")] == ["evt-1"]
    assert [r.getMessage() for r in caplog.records if r.name == "processor"][0].startswith("skipped events.raw[0]@0")


def test_unmapped_sample_is_repaired(world, sql):
    world.consumer.queue = [Msg(b'{"event_id":"e1","source_id":"s","received_at":"2026-11-20T10:15:02.123Z",'
                                b'"payload":{"x":"a\\u0000","y":NaN}}', 0)]
    world.proc.step()
    [sample] = sql("SELECT payload FROM outbox WHERE topic = 'schema.samples'")
    assert sample["payload"]["sample"] == {"x": "a�", "y": None}


def test_a_data_error_in_the_store_skips_only_that_record(world, sql, monkeypatch, caplog):
    real_open = world.store._open

    def open_(items):  # the batch applies the other records, then fails on evt-1
        out = real_open([it for it in items if it.envelope["event_id"] != "evt-1"])
        if len(out) < len(items):
            raise psycopg.DataError("rejected by the store")
        return out
    monkeypatch.setattr(world.store, "_open", open_)
    world.consumer.queue = [Msg(event("workflow", 0), 0), Msg(event("trade", 1), 1), Msg(event("metric", 2), 2)]
    with caplog.at_level(logging.WARNING, logger="processor"):
        world.proc.step()
    assert world.consumer.commits == [[("events.raw", 0, 3)]]
    assert {r["rule_id"] for r in sql("SELECT rule_id FROM alerts")} == {"settlements-workflow-failed", "anna-payments-critical"}
    assert len(changes(sql, "opened")) == 2  # the records applied before the error are not applied twice
    assert world.store.pg.info.transaction_status == psycopg.pq.TransactionStatus.IDLE
    assert any(r.getMessage().startswith("skipped events.raw[0]@1") for r in caplog.records)
    world.consumer.queue = [Msg(event("workflow", 3), 3)]
    world.proc.step()  # the connection still works
    assert world.consumer.commits[-1] == [("events.raw", 0, 4)]


# --- size of what is stored and shipped ---

def test_a_400_kb_event_gives_a_small_change_record_with_every_alert_field(world, sql):
    from processor.store import _ALERT_FIELDS
    e = event("workflow", 0)
    e["payload"]["error"]["message"] = "x" * 400_000
    e["payload"]["step"] = list(range(1000))
    world.consumer.queue = [Msg(e, 0)]
    world.proc.step()
    [record] = changes(sql, "opened")
    assert len(json.dumps(record, separators=(",", ":"), ensure_ascii=False).encode()) < 64 * 1024
    alert = record["alert"]
    assert set(alert) == set(_ALERT_FIELDS)
    assert len(alert["title"]) == 300 and alert["title"].endswith("…")
    assert len(alert["body"]) == 2000 and alert["body"].endswith("x…")
    assert len(alert["fields"]["error.message"]) == 500
    assert alert["fields"]["step"] == json.dumps(list(range(1000)), separators=(",", ":"))[:499] + "…"
    assert alert["fields"]["error.code"] == "TIMEOUT"
    raw = alert["sample_event"]["raw"]
    assert raw["_truncated"] is True and raw["_chars"] > 400_000 and len(raw["_preview"]) == 2000
    assert alert["sample_event"]["event_id"] == "evt-0"


def test_a_large_unmapped_sample_is_capped(world, sql):
    world.consumer.queue = [Msg(make_event("new-source", {"blob": "x" * 400_000}, 1), 0)]
    world.proc.step()
    [sample] = sql("SELECT payload FROM outbox WHERE topic = 'schema.samples'")
    assert sample["payload"]["sample"]["_truncated"] is True and len(sample["payload"]["sample"]["_preview"]) == 2000


# --- config.changes and commits ---

def test_config_records_are_committed_after_apply(world):
    record = {"kind": "rule", "id": "r1", "updated_at": "2026-11-20T09:30:00.000Z", "body": {"id": "r1"}}
    world.config_consumer.queue = [Msg(b"{bad", 3, topic="config.changes"), Msg(record, 4, topic="config.changes")]
    world.proc.step()
    assert world.calls[-2:] == ["config.apply", "commit"]
    assert world.config_consumer.commits == [[("config.changes", 0, 5)]]
    world.config_consumer.queue = [Msg(b"{bad", 5, topic="config.changes")]
    world.proc.step()  # nothing to apply, still committed
    assert world.config_consumer.commits[-1] == [("config.changes", 0, 6)]


def change(kind, actor, alert_id):
    return {"change_id": f"c-{kind}-{actor}", "change": kind, "at": "2026-11-20T10:16:00.000Z", "actor": actor,
            "alert": {"id": alert_id}}


def test_acks_and_resolves_by_people_are_applied_and_committed(world, sql, caplog):
    world.consumer.queue = [Msg(event("workflow", 0), 0)]
    world.proc.step()
    [alert] = sql("SELECT id FROM alerts")
    ignored = [change("opened", "processor", alert["id"]), change("resolved", "auto", alert["id"]),
               change("severity_raised", "marek", alert["id"]), b"{bad", change("acked", None, alert["id"]),
               {**change("resolved", "marek", alert["id"]), "alert": "x"}]
    world.changes_consumer.queue = [Msg(r, i, topic="alerts.changes") for i, r in enumerate(ignored)]
    with caplog.at_level(logging.WARNING, logger="processor"):
        world.proc.step()
    assert len([r for r in caplog.records if r.name == "processor"]) == 3  # bad JSON, no actor, no alert.id
    assert "store.apply_changes" not in world.calls
    assert world.changes_consumer.commits == [[("alerts.changes", 0, 6)]]
    assert sql("SELECT status FROM alerts") == [{"status": "open"}]
    world.changes_consumer.queue = [Msg(change("acked", "marek", alert["id"]), 6, topic="alerts.changes"),
                                    Msg(change("resolved", "anna", alert["id"]), 7, topic="alerts.changes")]
    world.proc.step()
    assert world.calls[-3:] == ["store.apply_changes", "commit", "relay.drain"]
    assert world.changes_consumer.commits[-1] == [("alerts.changes", 0, 8)]
    assert sql("SELECT status, acked_by, resolved_by FROM alerts") == [
        {"status": "resolved", "acked_by": "marek", "resolved_by": "anna"}]
    assert [c["change"] for c in changes(sql)] == ["opened"]


def rebalance(code):
    from confluent_kafka import KafkaError, KafkaException
    return KafkaException(KafkaError(code))


@pytest.mark.parametrize("which", ["consumer", "config_consumer"])
@pytest.mark.parametrize("code", ["REBALANCE_IN_PROGRESS", "ILLEGAL_GENERATION", "UNKNOWN_MEMBER_ID"])
def test_commit_during_a_rebalance_is_logged_and_the_loop_goes_on(world, caplog, which, code):
    from confluent_kafka import KafkaError
    getattr(world, which).fail = [rebalance(getattr(KafkaError, code))]
    world.consumer.queue = [Msg(event("workflow", 0), 0)]
    world.config_consumer.queue = [Msg(b"{bad", 0, topic="config.changes")]
    with caplog.at_level(logging.WARNING, logger="processor"):
        world.proc.step()
    assert "rebalance" in caplog.text
    world.consumer.queue = [Msg(event("workflow", 1), 1)]
    world.config_consumer.queue = [Msg(b"{bad", 1, topic="config.changes")]
    world.proc.step()
    assert world.consumer.commits[-1] == [("events.raw", 0, 2)]
    assert world.config_consumer.commits[-1] == [("config.changes", 0, 2)]


@pytest.mark.parametrize("which", ["consumer", "config_consumer"])
def test_other_commit_errors_still_end_the_process(world, which):
    from confluent_kafka import KafkaError, KafkaException
    getattr(world, which).fail = [rebalance(KafkaError._TRANSPORT)]
    world.consumer.queue = [Msg(event("workflow", 0), 0)]
    world.config_consumer.queue = [Msg(b"{bad", 0, topic="config.changes")]
    with pytest.raises(KafkaException):
        world.proc.step()


# --- main() ---

class Stop(Exception):
    pass


class KafkaStub:
    """Consumer and Producer stand-in for main(): records its config; the first consume ends the loop."""
    made = []

    def __init__(self, conf):
        self.conf = conf
        KafkaStub.made.append(self)

    def subscribe(self, topics, on_assign=None):
        self.topics, self.on_assign = topics, on_assign

    def assign(self, partitions):
        self.assigned = [(p.topic, p.partition, p.offset) for p in partitions]

    def consume(self, num_messages, timeout):
        raise Stop


@pytest.fixture
def main_env(db, monkeypatch, new_redis):
    import processor.main as M
    import redis
    import signal
    KafkaStub.made = []
    monkeypatch.setattr(M, "Consumer", KafkaStub)
    monkeypatch.setattr(M, "Producer", KafkaStub)
    monkeypatch.setattr(redis.Redis, "from_url", lambda *a, **k: new_redis())
    monkeypatch.setattr(signal, "signal", lambda *a: None)  # keep pytest's own handlers
    for k, v in {"KAFKA_BROKERS": "127.0.0.1:1", "DATABASE_URL": db, "REDIS_URL": "redis://unused"}.items():
        monkeypatch.setenv(k, v)
    monkeypatch.delenv("PROCESSOR_WORKERS", raising=False)
    return M


def test_kafka_clients_are_configured_for_safe_commits_and_delivery(main_env):
    from processor.relay import FLUSH_TIMEOUT_S
    with pytest.raises(Stop):
        main_env.main()
    consumers = {s.topics[0]: s.conf for s in KafkaStub.made if hasattr(s, "topics")}
    [producer] = [s.conf for s in KafkaStub.made if not hasattr(s, "topics")]
    assert consumers["events.raw"]["enable.auto.commit"] is False
    assert consumers["config.changes"]["enable.auto.commit"] is False
    assert {k: consumers["alerts.changes"][k] for k in ("group.id", "enable.auto.commit", "auto.offset.reset")} == {
        "group.id": "processor-changes", "enable.auto.commit": False, "auto.offset.reset": "latest"}
    assert consumers["events.raw"]["auto.offset.reset"] == consumers["config.changes"]["auto.offset.reset"] == "earliest"
    # every message of a drain gets its final delivery callback within that drain's flush
    assert producer["message.timeout.ms"] < FLUSH_TIMEOUT_S * 1000 and producer["enable.idempotence"] is True


def test_config_partitions_start_from_the_beginning_only_when_mappings_and_rules_are_empty(main_env, conns):
    from confluent_kafka import OFFSET_BEGINNING, OFFSET_INVALID, TopicPartition
    c = conns()
    c.execute("TRUNCATE mappings, rules, service_deps")
    with pytest.raises(Stop):
        main_env.main()
    [stub] = [s for s in KafkaStub.made if getattr(s, "topics", None) == ["config.changes"]]
    stub.on_assign(stub, [TopicPartition("config.changes", 0)])
    assert stub.assigned == [("config.changes", 0, OFFSET_BEGINNING)]
    c.execute("INSERT INTO rules (id, body, updated_at) VALUES ('r1', '{}', now())")
    stub.on_assign(stub, [TopicPartition("config.changes", 0)])
    assert stub.assigned == [("config.changes", 0, OFFSET_INVALID)]  # the committed offset
    c.execute("TRUNCATE rules")


def test_one_worker_by_default_runs_in_process(main_env, monkeypatch):
    ran = []
    monkeypatch.setattr(main_env, "_run", lambda *a: ran.append(a))
    monkeypatch.setattr(main_env.multiprocessing, "get_context", lambda *a: pytest.fail("spawned a worker"))
    main_env.main()
    assert len(ran) == 1


@pytest.mark.parametrize("value", ["0", "-2", "two", "1.5"])
def test_invalid_worker_count_fails_at_start(main_env, monkeypatch, value):
    monkeypatch.setenv("PROCESSOR_WORKERS", value)
    monkeypatch.setattr(main_env.psycopg, "connect", lambda *a, **k: pytest.fail("connected before validating"))
    with pytest.raises(SystemExit) as e:
        main_env.main()
    assert str(e.value) == f"processor: PROCESSOR_WORKERS must be a positive integer, got {value!r}"


def test_a_payload_timestamp_at_the_edge_of_the_range_falls_back_to_received_at(world, sql):
    e = event("workflow", 0)
    e["payload"]["timestamp"] = "9999-12-31T23:00:00Z"
    world.consumer.queue = [Msg(e, 0)]
    world.proc.step()
    [alert] = sql("SELECT first_seen, first_received_at FROM alerts")
    assert alert["first_seen"] == alert["first_received_at"]
