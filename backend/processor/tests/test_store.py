import dataclasses
import random
import threading
from datetime import UTC, datetime, timedelta

import psycopg
import pytest

from conftest import PAYLOADS, make_event
from processor import build_envelope, match_rules
from processor.store import DIRTY, FLUSH_LOCK, GRACE_S, RESOLVE_LOCK, Item, Store
from processor.store import _hash_key as store_hash

T0 = datetime(2026, 11, 20, 10, 15, 2, 123000, tzinfo=UTC)
ALERT_FIELDS = {"id", "rule_id", "team_id", "incident_id", "status", "severity", "title", "body", "fields", "color",
                "count", "first_seen", "last_seen", "first_received_at", "runbook_id", "acked_by", "resolved_by",
                "sample_event"}


@pytest.fixture
def new_store(db, sql, new_redis):
    conns = []

    def make():
        conns.append(psycopg.connect(db, autocommit=True))
        return Store(conns[-1], new_redis())
    yield make
    for c in conns:
        c.close()


@pytest.fixture
def batch(mappings, rules):
    by_id = {r.id: r.rule for r in rules}

    def make(name, offsets, partition=0, at=0.0, **payload):
        source, base = PAYLOADS[name]
        out = []
        for o in offsets:
            event = make_event(source, {**base, **payload}, f"{partition}-{o}")
            event["received_at"] = (T0 + timedelta(seconds=at)).isoformat(timespec="milliseconds")[:-6] + "Z"
            env = build_envelope(event, mappings)
            for m in match_rules(env, rules):
                out.append(Item(env, m, by_id[m.rule_id].window_s, by_id[m.rule_id].runbook_id, partition, o))
        return out
    return make


def changes(sql, kind=None):
    rows = [r["payload"] for r in sql("SELECT payload FROM outbox WHERE topic = 'alerts.changes' ORDER BY id")]
    return [r for r in rows if kind is None or r["change"] == kind]


def test_burst_is_one_alert_with_full_opened_record(new_store, batch, sql):
    store = new_store()
    for start in range(0, 500, 100):
        store.process(batch("workflow", range(start, start + 100)))
    store.flush()
    [alert] = sql("SELECT * FROM alerts")
    assert alert["count"] == 500 and alert["last_received_at"] == T0
    [opened] = changes(sql, "opened")
    assert opened["actor"] == "processor" and opened["at"] == "2026-11-20T10:15:02.123Z"
    a = opened["alert"]
    assert set(a) == ALERT_FIELDS
    assert a["id"] == alert["id"] and a["status"] == "open" and a["count"] == 1 and a["incident_id"] is None
    assert (a["rule_id"], a["team_id"], a["severity"], a["color"]) == (
        "settlements-workflow-failed", "settlements", "critical", "red")
    assert a["title"] == "order-settlement failed at step settle"
    assert a["fields"]["error.code"] == "TIMEOUT" and a["runbook_id"] == "settlement-timeout"
    assert (a["first_seen"], a["last_seen"], a["first_received_at"]) == (
        "2026-11-20T10:15:01.000Z", "2026-11-20T10:15:01.000Z", "2026-11-20T10:15:02.123Z")
    assert a["sample_event"]["event_id"] == "evt-0-0" and a["sample_event"]["mapped"] is True


def test_redelivery_changes_nothing(new_store, batch, sql):
    store = new_store()
    first, second = batch("workflow", range(10)), batch("workflow", range(10, 20))
    store.process(first)
    store.process(second)
    store.flush()
    before = (sql("SELECT count FROM alerts"), len(changes(sql)))
    store.process(second)
    store.process(first[5:6])
    store.process(first + second)
    store.flush()
    assert (sql("SELECT count FROM alerts"), len(changes(sql))) == before == ([{"count": 20}], 2)  # opened + threshold 10


def test_crash_before_redis_seed_does_not_double_open_or_count(new_store, new_redis, batch, sql):
    items = batch("workflow", range(10))
    new_store().process(items)
    new_redis().flushall()  # Postgres committed, Redis lost everything that came after
    store = new_store()
    store.process(items)
    store.flush()
    assert sql("SELECT count FROM alerts") == [{"count": 10}]
    assert len(changes(sql, "opened")) == 1


def test_two_replicas_race_on_a_new_key(new_store, batch, sql):
    stores, barrier = [new_store(), new_store()], threading.Barrier(2)

    def run(store, partition):
        barrier.wait()
        for start in range(0, 50, 10):
            store.process(batch("workflow", range(start, start + 10), partition=partition))

    threads = [threading.Thread(target=run, args=(s, p)) for p, s in enumerate(stores)]
    for t in threads:
        t.start()
    for t in threads:
        t.join()
    stores[0].flush()
    assert sql("SELECT count FROM alerts") == [{"count": 100}]
    assert len(changes(sql, "opened")) == 1


def race(stores, work) -> list:
    """Run work(store, i) for every store in its own thread, started together; return what they raised."""
    barrier, errors = threading.Barrier(len(stores)), []

    def run(store, i):
        barrier.wait()
        try:
            work(store, i)
        except Exception as e:
            errors.append(e)
    threads = [threading.Thread(target=run, args=(s, i)) for i, s in enumerate(stores)]
    for t in threads:
        t.start()
    for t in threads:
        t.join()
    return errors


def pricing(ns, offset, partition=0, at=0.0):
    """One event per instrument I<n>, in the order given; offsets offset+n."""
    return lambda batch: [it for n in ns for it in batch("pricing", [offset + n], partition=partition, at=at, instrument=f"I{n:03}")]


def test_two_replicas_race_on_many_overlapping_new_keys(new_store, batch, sql):
    def work(store, p):
        rng = random.Random(p)
        for rnd in range(10):  # each batch: 40 keys, 20 shared with the previous batch, shuffled differently per replica
            ns = list(range(rnd * 20, rnd * 20 + 40))
            rng.shuffle(ns)
            store.process(pricing(ns, rnd * 1000, partition=p)(batch))

    stores = [new_store(), new_store()]
    assert race(stores, work) == []
    stores[0].flush()
    assert sql("SELECT count(*) AS n, count(DISTINCT alert_key) AS k, sum(count) AS events FROM alerts WHERE status = 'open'") == [
        {"n": 220, "k": 220, "events": 2 * 10 * 40}]
    assert len(changes(sql, "opened")) == 220 and changes(sql, "reopened") == []


def test_six_replicas_open_shifted_overlapping_new_keys_without_giving_up_or_losing_counts(new_store, batch, sql):
    # issue #59: each open round can lose to a different replica; two events per key per batch catch a dropped miss
    def work(store, p):
        for rnd in range(5):
            ns = list(range(rnd * 200 + p * 10, rnd * 200 + p * 10 + 60))
            store.process(pricing(ns, rnd * 1000, partition=p)(batch) + pricing(ns, rnd * 1000 + 500, partition=p)(batch))

    stores = [new_store() for _ in range(6)]
    assert race(stores, work) == []
    stores[0].flush()
    assert sql("SELECT count(*) AS n, count(DISTINCT alert_key) AS k, sum(count) AS events FROM alerts") == [
        {"n": 550, "k": 550, "events": 6 * 5 * 60 * 2}]


def test_two_replicas_race_to_reopen_many_keys(new_store, new_redis, batch, sql):
    s0 = new_store()
    s0.process(pricing(range(60), 0)(batch))
    s0.flush()
    resolve_all(sql)
    new_redis().flushall()  # every key takes the miss path

    def work(store, p):
        ns = list(range(60))
        random.Random(p).shuffle(ns)
        store.process(pricing(ns, 100, partition=p + 1, at=5)(batch))

    stores = [new_store(), new_store()]
    assert race(stores, work) == []
    stores[0].flush()
    assert sql("SELECT count(*) AS n, sum(count) AS events FROM alerts WHERE status = 'open'") == [{"n": 60, "events": 180}]
    assert sql("SELECT count(*) AS n FROM alerts") == [{"n": 60}]
    assert len(changes(sql, "reopened")) == 60


def test_timed_flush_skips_while_another_replica_holds_the_lock(new_store, db, batch, sql):
    store = new_store()
    store.process(batch("workflow", range(5)))
    with psycopg.connect(db, autocommit=True) as other, other.transaction():
        other.execute("SELECT pg_advisory_xact_lock(%s)", (FLUSH_LOCK,))
        store.flush()
        assert sql("SELECT count FROM alerts") == [{"count": 1}]
        store.flush([batch("workflow", [0])[0].match.alert_key])  # targeted flushes never wait for the tick
        assert sql("SELECT count FROM alerts") == [{"count": 5}]
    store.process(batch("workflow", range(5, 7)))
    store.flush()
    assert sql("SELECT count FROM alerts") == [{"count": 7}]


def test_timed_flush_dying_after_its_redis_step_keeps_the_counts_and_releases_the_lock(new_store, batch, sql):
    store = new_store()
    store.process(batch("workflow", range(5)))
    orig, calls = store._exec, []

    def exec_(fill):
        calls.append(1)
        out = orig(fill)
        if len(calls) == 2:  # dies right after the Redis step (publish, undirty) of the flush
            raise Crash
        return out
    store._exec = exec_
    with pytest.raises(Crash):
        store.flush()
    new_store().flush()
    assert sql("SELECT count FROM alerts") == [{"count": 5}]  # the marks were cleared only after the commit
    assert sql("SELECT pg_try_advisory_lock(%s) AS ok", FLUSH_LOCK) == [{"ok": True}]  # released by the crashed flush
    sql("SELECT pg_advisory_unlock(%s) AS ok", FLUSH_LOCK)


def test_redis_wiped_mid_stream_continues_the_alert(new_store, new_redis, batch, sql):
    store = new_store()
    store.process(batch("workflow", range(30)))
    store.flush()
    new_redis().flushall()
    store.process(batch("workflow", range(30, 40)))
    assert sql("SELECT count FROM alerts") == [{"count": 30}]  # the continuing event is counted in Redis, not yet flushed
    store.flush()
    assert sql("SELECT count FROM alerts") == [{"count": 40}]
    assert len(changes(sql, "opened")) == 1


def test_severity_raise_is_immediate_and_once(new_store, batch, sql):
    store = new_store()
    store.process(batch("workflow", range(3), severity="warning"))
    store.flush()
    store.process(batch("workflow", [3], severity="critical"))  # no flush call: the raise flushes itself
    assert sql("SELECT severity FROM alerts") == [{"severity": "critical"}]
    store.process(batch("workflow", [4], severity="error"))
    store.flush()
    assert sql("SELECT severity, count FROM alerts") == [{"severity": "critical", "count": 5}]
    [raised] = changes(sql, "severity_raised")
    assert raised["alert"]["severity"] == "critical"
    assert [(c["change"], c["alert"]["severity"]) for c in changes(sql)] == [
        ("opened", "warning"), ("severity_raised", "critical")]


def test_count_thresholds(new_store, batch, sql):
    store = new_store()
    store.process(batch("pricing", range(5), instrument="EURUSD"))
    store.process(batch("pricing", range(5), partition=1, instrument="GBPUSD"))
    store.flush()
    assert changes(sql, "count_threshold") == []
    store.process(batch("pricing", range(5, 11), instrument="EURUSD"))
    store.flush()
    assert [c["alert"]["count"] for c in changes(sql, "count_threshold")] == [11]
    store.process(batch("pricing", range(5, 150), partition=1, instrument="GBPUSD"))
    store.flush()
    store.flush()
    assert [c["alert"]["count"] for c in changes(sql, "count_threshold")] == [11, 150]


def resolve_all(sql):
    sql("UPDATE alerts SET status = 'resolved', resolved_by = 'marek' RETURNING id")


@pytest.mark.parametrize("gap", [299, 599])  # inside window_s, and between window_s and 2 * window_s
def test_reopen_on_miss_path(new_store, new_redis, batch, sql, gap):
    store = new_store()
    store.process(batch("workflow", range(5)))
    store.flush()
    resolve_all(sql)
    new_redis().flushall()  # the hash is gone: the miss path decides
    store.process(batch("workflow", range(5, 8), at=gap))
    store.flush()
    [alert] = sql("SELECT * FROM alerts")
    assert (alert["status"], alert["count"], alert["resolved_by"]) == ("open", 8, None)
    [reopened] = changes(sql, "reopened")
    assert reopened["alert"]["id"] == alert["id"]


def test_reopen_on_flush_path(new_store, batch, sql):
    store = new_store()
    store.process(batch("workflow", range(5)))
    store.flush()
    resolve_all(sql)
    store.process(batch("workflow", range(5, 8), at=10))  # the hash still exists
    store.flush()
    [alert] = sql("SELECT * FROM alerts")
    assert (alert["status"], alert["count"]) == ("open", 8)
    [reopened] = changes(sql, "reopened")
    assert reopened["alert"]["id"] == alert["id"]


def test_event_after_two_windows_opens_a_new_alert(new_store, new_redis, batch, sql):
    store = new_store()
    store.process(batch("workflow", range(5)))
    store.flush()
    resolve_all(sql)
    new_redis().flushall()
    store.process(batch("workflow", range(5, 8), at=600))  # 2 * window_s after the last event
    store.flush()
    rows = sql("SELECT status, count FROM alerts ORDER BY first_received_at")
    assert rows == [{"status": "resolved", "count": 5}, {"status": "open", "count": 3}]
    assert len(changes(sql, "opened")) == 2 and changes(sql, "reopened") == []


def test_two_keys_are_two_alerts(new_store, batch, sql):
    store = new_store()
    store.process(batch("pricing", range(3), instrument="EURUSD") + batch("pricing", range(3, 6), instrument="GBPUSD"))
    store.flush()
    assert sql("SELECT title, count FROM alerts ORDER BY title") == [
        {"title": "Pricing errors on EURUSD", "count": 3}, {"title": "Pricing errors on GBPUSD", "count": 3}]
    assert len(changes(sql, "opened")) == 2


def test_one_batch_opens_and_reopens_many_keys_in_one_open_call(new_store, new_redis, batch, sql):
    store = new_store()
    store.process(batch("workflow", range(9)))
    store.flush()
    resolve_all(sql)
    new_redis().flushall()
    calls, orig = [], store._open
    store._open = lambda items: calls.append(len(items)) or orig(items)
    instruments = [f"I{n:02}" for n in range(50)]
    store.process(batch("workflow", [9], at=10) + [it for n, i in enumerate(instruments)
                                                   for it in batch("pricing", [2 * n, 2 * n + 1], instrument=i)])
    store.flush()
    assert calls == [51]
    assert sql("SELECT title, count FROM alerts WHERE rule_id = 'pricing-engine-errors' ORDER BY title") == [
        {"title": f"Pricing errors on {i}", "count": 2} for i in instruments]
    assert sorted(c["alert"]["title"] for c in changes(sql, "opened")[1:]) == [f"Pricing errors on {i}" for i in instruments]
    [reopened], [threshold] = changes(sql, "reopened"), changes(sql, "count_threshold")
    assert reopened["alert"]["id"] == threshold["alert"]["id"] and threshold["alert"]["count"] == 10
    assert reopened["at"] == "2026-11-20T10:15:12.123Z"
    assert sql("SELECT status, count FROM alerts WHERE rule_id = 'settlements-workflow-failed'") == [{"status": "open", "count": 10}]


def test_unmapped_schemas_are_sampled_once(new_store, mappings, sql):
    def env(n, payload):
        return build_envelope(make_event("vendor-x", payload, n), mappings)

    store = new_store()
    store.save_samples([(env(n, {"kind": "disk_full", "host": f"h{n}"}), []) for n in range(50)])
    store.save_samples([(env(50, {"kind": "disk_full", "host": "h50"}), [])])
    new_store().save_samples([(env(51, {"kind": "disk_full", "host": "h51"}), [])])  # another replica: ON CONFLICT
    [row] = sql("SELECT * FROM schema_samples")
    [record] = sql("SELECT key, payload FROM outbox WHERE topic = 'schema.samples'")
    assert record == {"key": row["fingerprint"], "payload": {
        "fingerprint": row["fingerprint"], "source_id": "vendor-x", "sample": {"kind": "disk_full", "host": "h0"},
        "seen_at": "2026-11-20T10:15:02.123Z"}}
    store.save_samples([(env(52, {"kind": "cpu_hot", "host": "h1"}), [])])
    store.save_samples([(build_envelope(make_event(*PAYLOADS["workflow"], 53), mappings), [])])  # mapped: skipped
    rows = sql("SELECT fingerprint, source_id FROM schema_samples ORDER BY fingerprint")
    records = sql("SELECT key FROM outbox WHERE topic = 'schema.samples' ORDER BY key")
    assert len(rows) == 2 and {r["source_id"] for r in rows} == {"vendor-x"}
    assert [r["key"] for r in records] == [r["fingerprint"] for r in rows]
    assert sql("SELECT count(*) AS n FROM parked_events") == [{"n": 53}]  # every unmapped event, the mapped one not


def test_unmapped_events_are_parked_once_capped_with_their_rules(new_store, mappings, sql, monkeypatch):
    import processor.store as S
    monkeypatch.setattr(S, "PARK_CAP", 3)

    def env(n, kind):
        return build_envelope(make_event("vendor-x", {"kind": kind, "n": n}, n), mappings)

    store = new_store()
    store.save_samples([(env(0, "disk_full"), ["r1", "r2"]), (env(1, "disk_full"), [])])
    store.save_samples([(env(0, "disk_full"), ["r1", "r2"])])  # redelivered: parked once
    store.save_samples([(env(n, "disk_full"), []) for n in range(2, 6)] + [(env(9, "cpu_hot"), [])])
    rows = sql("SELECT event_id, event, matched_rules FROM parked_events ORDER BY seq")
    assert [r["event_id"] for r in rows] == ["evt-0", "evt-1", "evt-2", "evt-9"]  # 3 per fingerprint
    assert rows[0]["matched_rules"] == ["r1", "r2"] and rows[1]["matched_rules"] == []
    assert rows[0]["event"] == {"event_id": "evt-0", "source_id": "vendor-x", "received_at": "2026-11-20T10:15:02.123Z",
                                "payload": {"kind": "disk_full", "n": 0}}


def test_parked_events_expire_with_the_resolve_tick(new_store, mappings, sql):
    from processor.store import PARK_TTL_S
    store = new_store()
    store.save_samples([(build_envelope(make_event("vendor-x", {"kind": "x"}, 0), mappings), [])])
    store.resolve_quiet(datetime.now(UTC) + timedelta(seconds=PARK_TTL_S - 60))
    assert len(sql("SELECT seq FROM parked_events")) == 1
    store.resolve_quiet(datetime.now(UTC) + timedelta(seconds=PARK_TTL_S + 1))
    assert sql("SELECT seq FROM parked_events") == []


def test_replayed_items_skip_the_watermark(new_store, batch, sql):
    from processor.store import REPLAY
    store = new_store()
    store.process(batch("workflow", range(100, 110)))
    # a lower seq after a higher one: seq order is not commit order across replicas
    store.process([dataclasses.replace(it, partition=REPLAY, offset=6) for it in batch("workflow", [0])])
    store.process([dataclasses.replace(it, partition=REPLAY, offset=5) for it in batch("workflow", [1])])
    store.process(batch("workflow", [109]))  # the real partition's watermark is untouched: still deduped
    store.flush()
    assert sql("SELECT count FROM alerts") == [{"count": 12}]


class Crash(Exception):
    pass


def crash_after_open(store):
    """The replica dies after `_open` commits to Postgres, before it seeds Redis."""
    orig = store._open

    def crash(items):
        orig(items)
        raise Crash
    store._open = crash


def test_crashed_opener_is_not_counted_again_after_another_partition_seeds(new_store, batch, sql):
    s1, s2 = new_store(), new_store()
    crash_after_open(s1)
    opener = batch("workflow", [0])
    with pytest.raises(Crash):
        s1.process(opener)
    s2.process(batch("workflow", [0], partition=1))  # same key from another partition seeds the hash first
    s2.process(opener)  # partition 0 redelivered
    s2.flush()
    assert sql("SELECT count FROM alerts") == [{"count": 2}]


def test_crashed_opener_redelivered_after_resolve_changes_nothing(new_store, batch, sql):
    s1 = new_store()
    crash_after_open(s1)
    opener = batch("workflow", [0])
    with pytest.raises(Crash):
        s1.process(opener)
    resolve_all(sql)
    s2 = new_store()
    s2.process(opener)
    s2.flush()
    assert sql("SELECT status, count FROM alerts") == [{"status": "resolved", "count": 1}]
    assert [c["change"] for c in changes(sql)] == ["opened"]


def test_stale_hash_after_reopen_race_does_not_block_other_keys(new_store, new_redis, batch, sql):
    store, r = new_store(), new_redis()
    store.process(batch("workflow", [0]))
    store.flush()
    resolve_all(sql)
    key = batch("workflow", [0])[0].match.alert_key
    stale = r.hgetall(store_hash(key))
    r.delete(store_hash(key))
    orig = store._open

    def other_replica_seeds_first(items):
        out = orig(items)
        r.hset(store_hash(key), mapping=stale)  # seeded from the old row a moment before this one
        return out
    store._open = other_replica_seeds_first
    store.process(batch("workflow", range(1, 4), at=700))  # > 2 * window_s: a second alert; hits go to the old one
    store._open = orig
    store.process(batch("pricing", range(5), instrument="EURUSD"))
    store.flush()
    assert sql("SELECT rule_id, status, count FROM alerts ORDER BY first_received_at, rule_id") == [
        {"rule_id": "pricing-engine-errors", "status": "open", "count": 5},
        {"rule_id": "settlements-workflow-failed", "status": "resolved", "count": 1},
        {"rule_id": "settlements-workflow-failed", "status": "open", "count": 1}]
    assert r.zrange(DIRTY, 0, -1) == []
    store.process(batch("workflow", [4], at=700))  # the next event finds the live alert
    store.flush()
    assert sql("SELECT status, count FROM alerts WHERE rule_id = 'settlements-workflow-failed' ORDER BY first_received_at") == [
        {"status": "resolved", "count": 1}, {"status": "open", "count": 2}]
    assert changes(sql, "reopened") == []


def run_between_flush_read_and_undirty(store, action):
    """The first Redis round trip of `flush` reads the hashes; run `action` right after it."""
    orig, done = store._exec, []

    def exec_(fill):
        out = orig(fill)
        if not done:
            done.append(1)
            action()
        return out
    store._exec = exec_


def test_event_counted_during_flush_stays_dirty(new_store, new_redis, batch, sql):
    s1, s2 = new_store(), new_store()
    s1.process(batch("workflow", range(5)))
    run_between_flush_read_and_undirty(s1, lambda: s2.process(batch("workflow", [0], partition=1)))
    s1.flush()
    assert sql("SELECT count FROM alerts") == [{"count": 5}]
    assert new_redis().zrange(DIRTY, 0, -1) != []
    s1.flush()
    assert sql("SELECT count FROM alerts") == [{"count": 6}]


def test_expired_hash_keeps_a_fresh_dirty_mark(new_store, new_redis, batch, sql):
    s1, s2 = new_store(), new_store()
    items = batch("workflow", range(5))
    s1.process(items)
    new_redis().delete(store_hash(items[0].match.alert_key))  # expired before a flush got to it
    run_between_flush_read_and_undirty(s1, lambda: s2.process(batch("workflow", range(5, 8), partition=1)))
    s1.flush()
    s1.flush()
    assert sql("SELECT count FROM alerts") == [{"count": 4}]  # opener + 3; the expired hash's unflushed 4 are gone


def test_concurrent_flushes_emit_one_threshold(new_store, batch, sql):
    s0 = new_store()
    s0.process(batch("workflow", range(5)))
    s0.flush()
    s0.process(batch("workflow", range(5, 11)))
    stores, barrier, errors = [new_store(), new_store()], threading.Barrier(2), []

    def change_after_both_read(*a):
        try:
            barrier.wait(0.5)  # with the row lock the other flush is blocked, so this times out
        except threading.BrokenBarrierError:
            pass
        Store._change(*a)

    def run(store):
        try:
            store.flush(keys)  # targeted: timed flushes take turns on FLUSH_LOCK, these race on the row lock
        except Exception as e:
            errors.append(e)
    keys = [batch("workflow", [0])[0].match.alert_key]
    for s in stores:
        s._change = change_after_both_read
    threads = [threading.Thread(target=run, args=(s,)) for s in stores]
    for t in threads:
        t.start()
    for t in threads:
        t.join()
    assert errors == []
    assert sql("SELECT count FROM alerts") == [{"count": 11}]
    assert [c["alert"]["count"] for c in changes(sql, "count_threshold")] == [11]


def test_reopen_on_miss_path_crossing_a_threshold(new_store, new_redis, batch, sql):
    store = new_store()
    store.process(batch("workflow", range(9)))
    store.flush()
    resolve_all(sql)
    new_redis().flushall()
    store.process(batch("workflow", [9], at=10))
    store.flush()
    assert sql("SELECT count FROM alerts") == [{"count": 10}]
    assert [c["alert"]["count"] for c in changes(sql, "count_threshold")] == [10]


# --- auto-resolve ---

def expire(r, items):
    """The hash's TTL ran out: no event was processed for this key for window_s."""
    r.delete(store_hash(items[0].match.alert_key))


def windowed(items, window_s):
    return [dataclasses.replace(it, window_s=window_s) for it in items]


def after(seconds):
    return T0 + timedelta(seconds=seconds)


def test_quiet_alert_is_auto_resolved_once(new_store, new_redis, batch, sql):
    store, items = new_store(), batch("workflow", range(3))
    store.process(items)
    store.flush()
    expire(new_redis(), items)
    now = after(300 + GRACE_S + 1)
    assert store.resolve_quiet(now) == 1
    assert sql("SELECT status, resolved_by FROM alerts") == [{"status": "resolved", "resolved_by": "auto"}]
    [record] = changes(sql, "resolved")
    assert (record["actor"], record["at"]) == ("auto", "2026-11-20T10:20:08.123Z")
    assert (record["alert"]["status"], record["alert"]["resolved_by"], record["alert"]["count"]) == ("resolved", "auto", 3)
    assert store.resolve_quiet(now) == 0
    assert len(changes(sql, "resolved")) == 1


@pytest.mark.parametrize("quiet_s, resolved", [(300 + GRACE_S - 1, 0), (300 + GRACE_S, 0), (300 + GRACE_S + 1, 1)])
def test_auto_resolve_cutoff(new_store, new_redis, batch, sql, quiet_s, resolved):
    store, items = new_store(), batch("workflow", range(3))
    store.process(items)
    store.flush()
    expire(new_redis(), items)
    assert store.resolve_quiet(after(quiet_s)) == resolved
    assert len(changes(sql, "resolved")) == resolved


def test_alert_still_receiving_a_backlog_is_not_resolved(new_store, batch, sql):
    store = new_store()
    store.process(batch("workflow", range(3)))
    store.flush()
    assert store.resolve_quiet(after(3600)) == 0  # quiet by ingest time, but its hash is alive
    assert sql("SELECT status FROM alerts") == [{"status": "open"}]


def test_acked_alert_is_auto_resolved(new_store, new_redis, batch, sql):
    store, items = new_store(), batch("workflow", range(3))
    store.process(items)
    store.flush()
    sql("UPDATE alerts SET status = 'acked', acked_by = 'marek' RETURNING id")
    expire(new_redis(), items)
    assert store.resolve_quiet(after(400)) == 1
    assert sql("SELECT status, acked_by, resolved_by FROM alerts") == [
        {"status": "resolved", "acked_by": "marek", "resolved_by": "auto"}]
    [record] = changes(sql, "resolved")
    assert record["actor"] == "auto" and record["alert"]["status"] == "resolved"


def test_each_rule_resolves_after_its_own_window(new_store, new_redis, batch, sql):
    store, r = new_store(), new_redis()
    fast, slow = windowed(batch("workflow", range(3)), 60), batch("pricing", range(3))
    store.process(fast + slow)
    store.flush()
    expire(r, fast)
    expire(r, slow)
    assert store.resolve_quiet(after(60 + GRACE_S + 1)) == 1
    assert sql("SELECT rule_id FROM alerts WHERE status = 'resolved'") == [{"rule_id": "settlements-workflow-failed"}]
    assert store.resolve_quiet(after(300 + GRACE_S + 1)) == 1
    assert [c["alert"]["rule_id"] for c in changes(sql, "resolved")] == ["settlements-workflow-failed", "pricing-engine-errors"]


def test_two_replicas_resolving_at_once_emit_one_record(new_store, new_redis, batch, sql):
    s0, items = new_store(), batch("workflow", range(3))
    s0.process(items)
    s0.flush()
    expire(new_redis(), items)
    stores, barrier, done, errors = [new_store(), new_store()], threading.Barrier(2), [], []

    def update_after_both_checked(store):
        orig = store._exec

        def exec_(fill):
            out = orig(fill)
            try:
                barrier.wait(0.5)  # with RESOLVE_LOCK the other replica skipped its tick, so this times out
            except threading.BrokenBarrierError:
                pass
            return out
        store._exec = exec_

    def run(store):
        try:
            done.append(store.resolve_quiet(after(400)))
        except Exception as e:
            errors.append(e)
    for s in stores:
        update_after_both_checked(s)
    threads = [threading.Thread(target=run, args=(s,)) for s in stores]
    for t in threads:
        t.start()
    for t in threads:
        t.join()
    assert errors == [] and sorted(done) == [0, 1]
    assert len(changes(sql, "resolved")) == 1


def test_resolve_tick_skips_while_another_replica_holds_the_lock(new_store, new_redis, db, batch, sql):
    store, items = new_store(), batch("workflow", range(3))
    store.process(items)
    store.flush()
    expire(new_redis(), items)
    with psycopg.connect(db, autocommit=True) as other:
        other.execute("SELECT pg_advisory_lock(%s)", (RESOLVE_LOCK,))
        assert store.resolve_quiet(after(400)) == 0
        assert sql("SELECT status FROM alerts") == [{"status": "open"}]
        other.execute("SELECT pg_advisory_unlock(%s)", (RESOLVE_LOCK,))
    assert store.resolve_quiet(after(400)) == 1
    assert len(changes(sql, "resolved")) == 1


def test_resolve_skips_an_alert_flushed_after_the_candidate_read(new_store, new_redis, batch, sql):
    store, items = new_store(), batch("workflow", range(3))
    store.process(items)
    store.flush()
    expire(new_redis(), items)
    orig = store._exec

    def late_event_flushed(fill):  # another replica flushes a late event between the hash check and the update
        out = orig(fill)
        sql("UPDATE alerts SET last_received_at = %s, count = 4 RETURNING id", after(399))
        return out
    store._exec = late_event_flushed
    assert store.resolve_quiet(after(400)) == 0
    assert sql("SELECT status FROM alerts") == [{"status": "open"}]


@pytest.mark.parametrize("gap, change", [(400, "reopened"), (600, "opened")])  # inside 2 * window_s, and past it
def test_event_after_auto_resolve(new_store, new_redis, batch, sql, gap, change):
    store, items = new_store(), batch("workflow", range(3))
    store.process(items)
    store.flush()
    expire(new_redis(), items)
    assert store.resolve_quiet(after(306)) == 1
    store.process(batch("workflow", [3], at=gap))
    store.flush()
    rows = sql("SELECT id, status FROM alerts ORDER BY first_received_at")
    [resolved] = changes(sql, "resolved")
    if change == "reopened":
        assert rows == [{"id": resolved["alert"]["id"], "status": "open"}]
        [reopened] = changes(sql, "reopened")
        assert reopened["alert"]["id"] == resolved["alert"]["id"]
    else:
        assert [r["status"] for r in rows] == ["resolved", "open"] and rows[0]["id"] == resolved["alert"]["id"]
        assert len(changes(sql, "opened")) == 2 and changes(sql, "reopened") == []


# --- acks and resolves made by people ---

def by(kind, actor, alert_id):
    return {"change_id": f"c-{kind}", "change": kind, "at": "2026-11-20T10:16:00.000Z", "actor": actor, "alert": {"id": alert_id}}


def test_ack_is_applied_once_and_the_alert_still_counts_and_auto_resolves(new_store, new_redis, batch, sql):
    store, items = new_store(), batch("workflow", range(3))
    store.process(items)
    store.flush()
    [alert] = sql("SELECT id FROM alerts")
    assert store.apply_changes([by("acked", "marek", alert["id"])]) == 1
    assert store.apply_changes([by("acked", "anna", alert["id"])]) == 0
    store.process(batch("workflow", range(3, 5)))
    store.flush()
    assert sql("SELECT status, acked_by, count FROM alerts") == [{"status": "acked", "acked_by": "marek", "count": 5}]
    assert [c["change"] for c in changes(sql)] == ["opened"]
    expire(new_redis(), items)
    assert store.resolve_quiet(after(400)) == 1
    assert sql("SELECT status, resolved_by FROM alerts") == [{"status": "resolved", "resolved_by": "auto"}]


def test_resolve_flushes_pending_counts_and_drops_the_hash(new_store, new_redis, batch, sql):
    store, r, items = new_store(), new_redis(), batch("workflow", range(3))
    store.process(items)
    store.flush()
    store.process(batch("workflow", range(3, 5)))  # counted in Redis, not flushed yet
    [alert] = sql("SELECT id FROM alerts")
    assert store.apply_changes([by("resolved", "marek", alert["id"])]) == 1
    assert sql("SELECT status, resolved_by, count FROM alerts") == [{"status": "resolved", "resolved_by": "marek", "count": 5}]
    assert not r.exists(store_hash(items[0].match.alert_key)) and r.zrange(DIRTY, 0, -1) == []
    assert [c["change"] for c in changes(sql)] == ["opened"]
    assert store.apply_changes([by("resolved", "anna", alert["id"]), by("acked", "anna", alert["id"]),
                                by("resolved", "anna", "no-such-alert")]) == 0
    assert sql("SELECT resolved_by FROM alerts") == [{"resolved_by": "marek"}]


@pytest.mark.parametrize("gap", [100, 600])  # inside 2 * window_s of the last event, and at it
def test_event_after_a_manual_resolve(new_store, batch, sql, gap):
    store = new_store()
    store.process(batch("workflow", range(3)))
    store.flush()
    [alert] = sql("SELECT id FROM alerts")
    store.apply_changes([by("resolved", "marek", alert["id"])])
    store.process(batch("workflow", [3], at=gap))
    store.flush()
    rows = sql("SELECT id, status, count FROM alerts ORDER BY first_received_at")
    if gap == 100:
        assert rows == [{"id": alert["id"], "status": "open", "count": 4}]
        assert [c["change"] for c in changes(sql)] == ["opened", "reopened"]
    else:
        assert [(r["id"] == alert["id"], r["status"], r["count"]) for r in rows] == [(True, "resolved", 3), (False, "open", 1)]
        assert [c["change"] for c in changes(sql)] == ["opened", "opened"]


def test_window_s_follows_the_rule_on_insert_and_reopen(new_store, new_redis, batch, sql):
    store = new_store()
    store.process(windowed(batch("workflow", range(2)), 60))
    store.flush()
    assert sql("SELECT window_s FROM alerts") == [{"window_s": 60}]
    resolve_all(sql)
    new_redis().flushall()
    store.process(windowed(batch("workflow", [2], at=100), 120))  # the rule's window changed in between
    store.flush()
    assert sql("SELECT status, window_s FROM alerts") == [{"status": "open", "window_s": 120}]
