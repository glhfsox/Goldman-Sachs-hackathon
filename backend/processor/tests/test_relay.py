import json
import threading
import time

import psycopg
import pytest
from psycopg.types.json import Jsonb

from confluent_kafka import KafkaError, KafkaException

from processor.relay import Relay


class FakeProducer:
    """confluent_kafka.Producer subset. `fail`: key -> "error" (failed callback), "timeout" (no callback, still
    queued after flush), "too_large" (produce raises MSG_SIZE_TOO_LARGE) or "full" (produce raises BufferError)."""

    def __init__(self, sent=None, fail=None, delay=0.0):
        self.sent, self.fail, self.delay, self.queue = [] if sent is None else sent, fail or {}, delay, []
        self.produced = 0

    def produce(self, topic, key=None, value=None, on_delivery=None):
        if self.fail.get(key) == "too_large":
            raise KafkaException(KafkaError(KafkaError.MSG_SIZE_TOO_LARGE))
        if self.fail.get(key) == "full":
            raise BufferError("Local: Queue full")
        self.produced += 1
        self.queue.append((topic, key, value, on_delivery))

    def flush(self, timeout=None):
        time.sleep(self.delay)
        left = []
        for topic, key, value, cb in self.queue:
            if self.fail.get(key) == "timeout":
                left.append((topic, key, value, cb))
            elif self.fail.get(key) == "error":
                cb("broker down", None)
            else:
                self.sent.append((topic, key, value))
                cb(None, None)
        self.queue = left
        return len(left)


class BrokenRedis:
    def pipeline(self, transaction=True):
        raise ConnectionError("redis down")


def change(alert_id, kind="opened"):
    return {"change_id": f"c-{alert_id}-{kind}", "change": kind, "at": "2026-11-20T10:15:02.180Z",
            "actor": "processor", "alert": {"id": alert_id, "title": "Zürich"}}


@pytest.fixture
def outbox(sql):
    def add(*rows):
        for topic, key, payload in rows:
            sql("INSERT INTO outbox (topic, key, payload) VALUES (%s, %s, %s) RETURNING id", topic, key, Jsonb(payload))
    return add


@pytest.fixture
def conns(db):
    opened = []

    def make():
        opened.append(psycopg.connect(db))
        return opened[-1]
    yield make
    for c in opened:
        c.close()


def keys(sql):
    return [r["key"] for r in sql("SELECT key FROM outbox ORDER BY id")]


def test_rows_go_out_in_id_order_and_are_deleted(outbox, conns, new_redis, sql):
    rows = [("alerts.changes", "a1", change("a1")), ("schema.samples", "fp1", {"fingerprint": "fp1"}),
            ("alerts.changes", "a1", change("a1", "severity_raised"))]
    outbox(*rows)
    producer = FakeProducer()
    assert Relay(conns(), producer, new_redis()).drain() == 3
    assert [(t, k, json.loads(v)) for t, k, v in producer.sent] == rows
    value = producer.sent[0][2]  # jsonb reorders keys, so compare compactness and encoding on the bytes
    assert b", " not in value and b'": ' not in value and "Zürich".encode() in value
    assert keys(sql) == []


@pytest.mark.parametrize("mode", ["error", "timeout"])
def test_failed_delivery_keeps_that_row_and_later_ones(outbox, conns, new_redis, sql, mode):
    outbox(*[("alerts.changes", k, change(k)) for k in ("a1", "a2", "a3", "a4")])
    conn, r = conns(), new_redis()
    assert Relay(conn, FakeProducer(fail={"a2": mode}), r).drain() == 1
    assert keys(sql) == ["a2", "a3", "a4"]
    producer = FakeProducer()
    assert Relay(conn, producer, r).drain() == 3
    assert [k for _, k, _ in producer.sent] == ["a2", "a3", "a4"]
    assert keys(sql) == []


def test_limit_takes_the_oldest_rows(outbox, conns, new_redis, sql):
    outbox(*[("alerts.changes", k, change(k)) for k in ("a1", "a2", "a3")])
    assert Relay(conns(), FakeProducer(), new_redis()).drain(limit=2) == 2
    assert keys(sql) == ["a3"]


def test_alert_changes_reach_ui_alerts_and_samples_do_not(outbox, conns, new_redis):
    r = new_redis()
    sub = r.pubsub(ignore_subscribe_messages=True)
    sub.subscribe("ui.alerts")
    sub.get_message(timeout=1)
    outbox(("schema.samples", "fp1", {"fingerprint": "fp1"}), ("alerts.changes", "a1", change("a1")))
    assert Relay(conns(), FakeProducer(), r).drain() == 2
    messages = []
    while m := sub.get_message(timeout=0.2):
        messages.append(json.loads(m["data"]))
    assert messages == [{"type": "alert", "change": "opened", "alert": {"id": "a1", "title": "Zürich"}}]


def test_redis_failure_does_not_keep_the_row(outbox, conns, sql):
    outbox(("alerts.changes", "a1", change("a1")))
    assert Relay(conns(), FakeProducer(), BrokenRedis()).drain() == 1
    assert keys(sql) == []


def test_empty_outbox_returns_zero(sql, conns, new_redis):
    assert Relay(conns(), FakeProducer(), new_redis()).drain() == 0


def test_two_replicas_never_publish_a_row_twice(outbox, conns, new_redis, sql):
    outbox(*[("alerts.changes", f"a{i}", change(f"a{i}")) for i in range(20)])
    sent, barrier, results = [], threading.Barrier(2), []
    relays = [Relay(conns(), FakeProducer(sent, delay=0.3), new_redis()) for _ in range(2)]

    def run(relay):
        barrier.wait()
        results.append(relay.drain())

    threads = [threading.Thread(target=run, args=(r,)) for r in relays]
    for t in threads:
        t.start()
    for t in threads:
        t.join()
    assert sorted(results) == [0, 20]
    assert sorted(k for _, k, _ in sent) == sorted(f"a{i}" for i in range(20))
    assert keys(sql) == []


def test_a_row_kafka_rejects_for_good_is_moved_aside_and_the_rest_go_out(outbox, conns, new_redis, sql, caplog):
    outbox(*[("alerts.changes", k, change(k)) for k in ("a1", "a2", "a3")])
    producer = FakeProducer(fail={"a2": "too_large"})
    assert Relay(conns(), producer, new_redis()).drain() == 2
    assert [k for _, k, _ in producer.sent] == ["a1", "a3"]
    assert keys(sql) == []
    [dead] = sql("SELECT id, topic, key, payload, error, failed_at FROM outbox_dead")
    assert (dead["id"], dead["topic"], dead["key"], dead["payload"]) == (2, "alerts.changes", "a2", change("a2"))
    assert "MSG_SIZE_TOO_LARGE" in dead["error"] and dead["failed_at"] is not None
    assert any(r.levelname == "ERROR" and "outbox row 2" in r.getMessage() for r in caplog.records)


def test_a_full_queue_keeps_that_row_and_later_ones(outbox, conns, new_redis, sql):
    outbox(*[("alerts.changes", k, change(k)) for k in ("a1", "a2", "a3")])
    assert Relay(conns(), FakeProducer(fail={"a2": "full"}), new_redis()).drain() == 1
    assert keys(sql) == ["a2", "a3"] and sql("SELECT * FROM outbox_dead") == []


def test_messages_still_queued_after_flush_count_as_failed(outbox, conns, new_redis, sql, caplog):
    outbox(*[("alerts.changes", k, change(k)) for k in ("a1", "a2", "a3")])
    producer = FakeProducer(fail={"a2": "timeout"})
    assert Relay(conns(), producer, new_redis()).drain() == 1
    assert producer.produced == 3 and keys(sql) == ["a2", "a3"]
    assert any("1 message(s) still queued after flush" in r.getMessage() for r in caplog.records)
