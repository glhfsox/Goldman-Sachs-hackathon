"""State layer: rule matches -> deduplicated alerts (OVERVIEW "How one event flows", steps 4-6).

Postgres decides whether an alert exists; Redis only caches it and counts events between flushes.
Every change record goes to the `outbox` table in the same transaction as the alert row.
"""

import json
import uuid
from dataclasses import dataclass
from datetime import UTC, datetime, timedelta

import psycopg
from psycopg.rows import dict_row
from psycopg.types.json import Jsonb
from redis.exceptions import NoScriptError

from .core import _EPOCH, SEV_RANK, RuleMatch, fingerprint
from .models import SEVERITIES

PREFIX = "proc:"
DIRTY = PREFIX + "dirty"  # sorted set: alert key -> count at its last bump
TOPIC = "alerts.changes"
SAMPLES_TOPIC = "schema.samples"
UI_CHANNEL = "ui.alerts"
THRESHOLDS = (1000, 100, 10)  # highest first: one count_threshold per flush
# Caps on what an alert row stores and every change record ships; rules still see the full payload.
TITLE_MAX, BODY_MAX, FIELD_MAX, RAW_MAX, PREVIEW_MAX = 300, 2000, 500, 16384, 2000
MISS, RAISED = 0, 3
FLUSH_LOCK, RESOLVE_LOCK = 0x666C757368, 0x7265736F6C7665  # "flush", "resolve"; relay.LOCK_ID is the outbox's
GRACE_S = 5  # auto-resolve waits this long past window_s: covers the lag of the once-per-second flush

# KEYS: alert hash, DIRTY. ARGV: partition, offset, last_seen ms, received ms, severity rank, ttl s, alert key.
_HIT = """
if redis.call('EXISTS', KEYS[1]) == 0 then return 0 end
local off = 'off:' .. ARGV[1]
local w = redis.call('HGET', KEYS[1], off)
if w and tonumber(ARGV[2]) <= tonumber(w) then return 1 end
local n = redis.call('HINCRBY', KEYS[1], 'count', 1)
redis.call('HSET', KEYS[1], off, ARGV[2])
if tonumber(ARGV[3]) > tonumber(redis.call('HGET', KEYS[1], 'ls')) then redis.call('HSET', KEYS[1], 'ls', ARGV[3]) end
if tonumber(ARGV[4]) > tonumber(redis.call('HGET', KEYS[1], 'lr')) then redis.call('HSET', KEYS[1], 'lr', ARGV[4]) end
local raised = tonumber(ARGV[5]) > tonumber(redis.call('HGET', KEYS[1], 'sev'))
if raised then redis.call('HSET', KEYS[1], 'sev', ARGV[5]) end
redis.call('EXPIRE', KEYS[1], ARGV[6])
redis.call('ZADD', KEYS[2], n, ARGV[7])
if raised then return 3 end
return 2
"""

# KEYS: alert hash. ARGV: id, count, last_seen ms, received ms, severity rank, ttl s, opened partition, opened offset.
# The opener's watermark is set on every seed: it is in the row's count, whoever seeds first.
_SEED = """
if redis.call('EXISTS', KEYS[1]) == 0 then
  redis.call('HSET', KEYS[1], 'id', ARGV[1], 'count', ARGV[2], 'ls', ARGV[3], 'lr', ARGV[4], 'sev', ARGV[5])
  redis.call('EXPIRE', KEYS[1], ARGV[6])
end
local off = 'off:' .. ARGV[7]
local w = redis.call('HGET', KEYS[1], off)
if not w or tonumber(ARGV[8]) > tonumber(w) then redis.call('HSET', KEYS[1], off, ARGV[8]) end
return 1
"""

# KEYS: DIRTY. ARGV: alert key, the count that was flushed. Keeps the mark if events arrived meanwhile.
_UNDIRTY = """
if tonumber(redis.call('ZSCORE', KEYS[1], ARGV[1])) == tonumber(ARGV[2]) then
  return redis.call('ZREM', KEYS[1], ARGV[1])
end
return 0
"""

# KEYS: alert hash, DIRTY. ARGV: alert key, id or ''. Drops the hash and its mark only if no replica re-seeded it meanwhile.
_DROP = """
if (redis.call('HGET', KEYS[1], 'id') or '') == ARGV[2] then
  redis.call('DEL', KEYS[1])
  return redis.call('ZREM', KEYS[2], ARGV[1])
end
return 0
"""

_UPDATE = (
    "UPDATE alerts SET status = %(status)s, severity = %(severity)s, count = %(count)s,"
    " last_seen = %(last_seen)s, last_received_at = %(last_received_at)s,"
    " acked_by = %(acked_by)s, resolved_by = %(resolved_by)s WHERE id = %(id)s")

_REOPEN = (
    "UPDATE alerts SET status = 'open', acked_by = NULL, resolved_by = NULL, count = count + 1,"
    " severity = %s, last_seen = GREATEST(last_seen, %s), last_received_at = GREATEST(last_received_at, %s),"
    " opened_by_event = %s, opened_partition = %s, opened_offset = %s, window_s = %s WHERE id = %s RETURNING *")

_INSERT = (
    "INSERT INTO alerts (id, alert_key, rule_id, team_id, status, severity, title, body, fields,"
    " color, count, first_seen, last_seen, first_received_at, last_received_at, runbook_id,"
    " sample_event, opened_by_event, opened_partition, opened_offset, window_s)"
    " VALUES (%s, %s, %s, %s, 'open', %s, %s, %s, %s, %s, 1, %s, %s, %s, %s, %s, %s, %s, %s, %s, %s) RETURNING *")

_ALERT_FIELDS = (
    "id", "rule_id", "team_id", "incident_id", "status", "severity", "title", "body", "fields", "color",
    "count", "first_seen", "last_seen", "first_received_at", "runbook_id", "acked_by", "resolved_by",
    "sample_event",
)


@dataclass(slots=True, frozen=True)
class Item:
    """One rule match of one Kafka record."""
    envelope: dict
    match: RuleMatch
    window_s: int
    runbook_id: str | None
    partition: int
    offset: int


def _ms(t: str | datetime) -> int:
    """Epoch milliseconds, in integers so no float rounding shifts a timestamp."""
    return ((datetime.fromisoformat(t) if type(t) is str else t) - _EPOCH) // timedelta(milliseconds=1)


def _dt(ms: str | int) -> datetime:
    return _EPOCH + timedelta(milliseconds=int(ms))


def _iso(dt: datetime) -> str:
    return dt.astimezone(UTC).isoformat(timespec="milliseconds")[:-6] + "Z"


def alert_object(row: dict) -> dict:
    """An `alerts` row -> the Alert object of contracts/api.md."""
    return {k: _iso(row[k]) if isinstance(row[k], datetime) else row[k] for k in _ALERT_FIELDS}


def _cut(s: str, n: int) -> str:
    return s if len(s) <= n else s[:n - 1] + "…"


def _compact(v) -> str:
    return json.dumps(v, separators=(",", ":"), ensure_ascii=False)


def _capped_field(v):
    if type(v) is str:
        return _cut(v, FIELD_MAX)
    return v if len(s := _compact(v)) <= FIELD_MAX else _cut(s, FIELD_MAX)


def _capped_raw(raw):
    if len(s := _compact(raw)) <= RAW_MAX:
        return raw
    return {"_truncated": True, "_chars": len(s), "_preview": _cut(s, PREVIEW_MAX)}


def _hash_key(alert_key: str) -> str:
    return PREFIX + "a:" + alert_key


class Store:
    """Writes alerts for one processor replica. `redis` must be created with decode_responses=True."""

    def __init__(self, conn: psycopg.Connection, redis):
        self.pg, self.r = conn, redis
        self._seen: set[str] = set()  # fingerprints already in schema_samples
        self._load()

    def _load(self):
        self._sha = {name: self.r.script_load(src) for name, src in (("hit", _HIT), ("seed", _SEED), ("undirty", _UNDIRTY), ("drop", _DROP))}

    def _exec(self, fill) -> list:
        """One pipeline round trip; reloads the scripts once if Redis lost them (every call here is idempotent)."""
        for retry in (False, True):
            pipe = self.r.pipeline(transaction=False)
            fill(pipe)
            try:
                return pipe.execute()
            except NoScriptError:
                if retry:
                    raise
                self._load()

    # --- hot path ---

    def process(self, items: list[Item]) -> None:
        """Apply one Kafka batch. Replaying any part of an applied batch changes nothing."""
        # shortcut: counts not yet flushed are lost if Redis loses its data, run Redis with AOF when that matters
        # shortcut: watermarks lost with Redis mean one replayed batch can be counted twice, keep them in Postgres if exact counts matter
        todo, seeds, raised = items, [], set()
        while todo or seeds:
            def fill(pipe, todo=todo, seeds=seeds):
                for row, it in seeds:
                    pipe.evalsha(self._sha["seed"], 1, _hash_key(row["alert_key"]),
                                 row["id"], row["count"], _ms(row["last_seen"]), _ms(row["last_received_at"]), SEV_RANK[row["severity"]],
                                 it.window_s, row["opened_partition"], row["opened_offset"])
                for it in todo:
                    env, key = it.envelope, it.match.alert_key
                    pipe.evalsha(self._sha["hit"], 2, _hash_key(key), DIRTY, it.partition, it.offset,
                                 _ms(env["ts"]), _ms(env["received_at"]), SEV_RANK[env["severity"]],
                                 it.window_s, key)
            results = self._exec(fill)[len(seeds):]
            first, again, seeds = {}, set(), []
            for i, (it, res) in enumerate(zip(todo, results)):
                key = it.match.alert_key
                if res == MISS:
                    if key in first:
                        again.add(i)  # counted by the hot path once the first miss has seeded the hash
                    else:
                        first[key] = i
                elif res == RAISED:
                    raised.add(key)
            for i, (row, counted) in zip(first.values(), self._open([todo[i] for i in first.values()]) if first else []):
                seeds.append((row, todo[i]))
                if not counted:
                    again.add(i)
            todo = [it for i, it in enumerate(todo) if i in again]  # keeps partition order for the watermarks
        if raised:
            self.flush(raised)  # a severity raise should not wait for the next flush tick

    def _open(self, items: list[Item]) -> list[tuple[dict, bool]]:
        """Miss path for items with distinct alert keys: find, reopen or insert each alert, all in one transaction with
        a fixed number of round trips. Returns, in item order, the row and whether that event is already in its count."""
        keys = [it.match.alert_key for it in items]
        for _ in range(3):
            try:
                with self.pg.transaction(), self.pg.cursor(row_factory=dict_row) as cur:
                    cur.execute("SELECT * FROM alerts WHERE alert_key = ANY(%s) AND status <> 'resolved'", (keys,))
                    out = {row["alert_key"]: row for row in cur.fetchall()}
                    # the hash expired or Redis lost it; a redelivered opener is already counted
                    out = {k: (row, row["opened_by_event"] == it.envelope["event_id"])
                           for it in items if (row := out.get(k := it.match.alert_key))}
                    rest = sorted((it for it in items if it.match.alert_key not in out), key=lambda it: it.match.alert_key)
                    # the latest resolved row per key, locked in id order like flush; the outer status re-checks each row once locked
                    cur.execute(
                        "SELECT * FROM alerts WHERE status = 'resolved' AND id IN (SELECT DISTINCT ON (alert_key) id FROM alerts"
                        " WHERE alert_key = ANY(%s) AND status = 'resolved' ORDER BY alert_key, last_received_at DESC)"
                        " ORDER BY id FOR UPDATE", ([it.match.alert_key for it in rest],))
                    olds = {row["alert_key"]: row for row in cur.fetchall()}
                    reopens, inserts, at = [], [], {}  # at: alert key -> received_at of its event
                    for it in rest:
                        env, m, old = it.envelope, it.match, olds.get(it.match.alert_key)
                        received = at[m.alert_key] = datetime.fromisoformat(env["received_at"])
                        if old and old["opened_by_event"] == env["event_id"]:
                            out[m.alert_key] = old, True  # a redelivered opener of an alert resolved since: already applied
                        # rules.md §4: auto-resolve after window_s of silence, reopen within another window_s
                        elif old and received - old["last_received_at"] < timedelta(seconds=2 * it.window_s):
                            reopens.append((max(old["severity"], env["severity"], key=SEV_RANK.get), env["ts"], received,
                                            env["event_id"], it.partition, it.offset, it.window_s, old["id"]))
                        else:
                            inserts.append((
                                str(uuid.uuid4()), m.alert_key, m.rule_id, m.team_id, env["severity"],
                                _cut(m.title, TITLE_MAX), _cut(m.body, BODY_MAX),
                                Jsonb({k: _capped_field(v) for k, v in m.fields.items()}), m.color, env["ts"], env["ts"],
                                received, received, it.runbook_id, Jsonb({**env, "raw": _capped_raw(env["raw"])}),
                                env["event_id"], it.partition, it.offset, it.window_s))
                    records = []

                    def opened(change, rows):
                        for row in rows:
                            records.append(self._record(row, change, at[row["alert_key"]]))
                            if row["count"] in THRESHOLDS:  # a reopen adds 1, so it crosses a threshold only by landing on it
                                records.append(self._record(row, "count_threshold", at[row["alert_key"]]))
                            out[row["alert_key"]] = row, True
                    # executemany pipelines the statements: one round trip per list, however many alerts; inserts in key order
                    for change, query, params in (("reopened", _REOPEN, reopens), ("opened", _INSERT, inserts)):
                        if params:
                            cur.executemany(query, params, returning=True)
                            opened(change, [cur.fetchone() for _ in cur.results()])
                    if records:
                        cur.executemany("INSERT INTO outbox (topic, key, payload) VALUES (%s, %s, %s)", records)
                    return [out[k] for k in keys]
            # another replica opened one of these first, or locked the same rows the other way round; the next round finds its rows
            except (psycopg.errors.UniqueViolation, psycopg.errors.DeadlockDetected):
                continue
        raise RuntimeError(f"could not open alerts for {len(keys)} keys, first {min(keys)}")

    @staticmethod
    def _record(row: dict, change: str, at: datetime, actor: str = "processor") -> tuple:
        """Outbox row of a change record (events.md §4); the API builds the timeline from these."""
        record = {"change_id": str(uuid.uuid4()), "change": change, "at": _iso(at), "actor": actor,
                  "alert": alert_object(row)}
        return TOPIC, row["id"], Jsonb(record)

    @staticmethod
    def _change(cur, row: dict, change: str, at: datetime, actor: str = "processor") -> None:
        cur.execute("INSERT INTO outbox (topic, key, payload) VALUES (%s, %s, %s)", Store._record(row, change, at, actor))

    # --- flush ---

    def flush(self, keys=None) -> None:
        """Bring Postgres up to date with the Redis counters of dirty alerts. Call about once per second."""
        if keys is None:
            # one replica flushes the shared DIRTY set at a time, the others skip the tick instead of queueing on its row locks;
            # a session lock, so the flush still commits before it clears the dirty marks
            if self.pg.execute("SELECT pg_try_advisory_lock(%s)", (FLUSH_LOCK,)).fetchone()[0]:
                try:
                    self.flush(self.r.zrange(DIRTY, 0, -1))
                finally:
                    self.pg.execute("SELECT pg_advisory_unlock(%s)", (FLUSH_LOCK,))
            return
        keys = list(keys)
        if not keys:
            return
        hashes = dict(zip(keys, self._exec(lambda pipe: [pipe.hgetall(_hash_key(k)) for k in keys])))
        live = {k: h for k, h in hashes.items() if h}
        messages, stale = [], {}
        if live:
            with self.pg.transaction(), self.pg.cursor(row_factory=dict_row) as cur:
                # locked in id order so two replicas flushing the same alerts neither deadlock nor emit twice
                cur.execute("SELECT * FROM alerts WHERE id = ANY(%s) ORDER BY id FOR UPDATE",
                            ([h["id"] for h in live.values()],))
                rows = {row["id"]: row for row in cur.fetchall()}
                updates = []
                for k, h in live.items():
                    if (row := rows.get(h["id"])) is None:
                        continue
                    old_count, old_sev = row["count"], row["severity"]
                    count = max(old_count, int(h["count"]))
                    last_seen, last_received = max(row["last_seen"], _dt(h["ls"])), max(row["last_received_at"], _dt(h["lr"]))
                    changes = []
                    if count > old_count and row["status"] == "resolved":  # resolved while events kept coming
                        changes.append("reopened")
                        row.update(status="open", acked_by=None, resolved_by=None)
                    if int(h["sev"]) > SEV_RANK[old_sev]:
                        changes.append("severity_raised")
                        row["severity"] = SEVERITIES[int(h["sev"])]
                    if any(old_count < t <= count for t in THRESHOLDS):
                        changes.append("count_threshold")
                    if not changes and (count, last_seen, last_received) == (old_count, row["last_seen"], row["last_received_at"]):
                        continue
                    row.update(count=count, last_seen=last_seen, last_received_at=last_received)
                    if "reopened" in changes:
                        try:
                            with self.pg.transaction():  # savepoint: a reopen that collides must not undo the other keys
                                cur.execute(_UPDATE, row)
                        except psycopg.errors.UniqueViolation:
                            stale[k] = h["id"]  # another alert went live under this key; its next event finds that one
                            continue
                    else:
                        updates.append(row)
                    for change in changes:
                        self._change(cur, row, change, last_received)
                    if count > old_count:
                        messages.append({"type": "count", "alert_id": row["id"], "count": count, "last_seen": _iso(last_seen)})
                if updates:
                    cur.executemany(_UPDATE, updates)

        def fill(pipe):
            for m in messages:
                pipe.publish(UI_CHANNEL, json.dumps(m))
            for k, h in hashes.items():
                if k in stale or not h:  # expired: nothing left to flush; its next event marks it again
                    pipe.evalsha(self._sha["drop"], 2, _hash_key(k), DIRTY, k, stale.get(k, ""))
                else:
                    pipe.evalsha(self._sha["undirty"], 1, DIRTY, k, h["count"])
        self._exec(fill)

    # --- auto-resolve ---

    def resolve_quiet(self, now: datetime) -> int:
        """Resolve alerts with no event for their rule's window_s (rules.md §4); return how many. Call every few seconds."""
        # one replica scans and resolves at a time, the others skip this tick instead of locking the same rows
        if not self.pg.execute("SELECT pg_try_advisory_lock(%s)", (RESOLVE_LOCK,)).fetchone()[0]:
            return 0
        try:
            return self._resolve_quiet(now)
        finally:
            self.pg.execute("SELECT pg_advisory_unlock(%s)", (RESOLVE_LOCK,))

    def _resolve_quiet(self, now: datetime) -> int:
        quiet = {"now": now, "grace": GRACE_S}
        cond = "status <> 'resolved' AND last_received_at < %(now)s - make_interval(secs => window_s + %(grace)s)"
        rows = self.pg.execute(f"SELECT id, alert_key FROM alerts WHERE {cond}", quiet).fetchall()
        if not rows:
            return 0
        # last_received_at is ingest time, so a backlog looks quiet by it; the hash TTL counts real time since the last processed event
        alive = self._exec(lambda pipe: [pipe.exists(_hash_key(key)) for _, key in rows])
        ids = [row_id for (row_id, _), a in zip(rows, alive) if not a]
        if not ids:
            return 0
        # shortcut: an event between the hash check and this update leaves the alert resolved with a fresh hash; the flush-path reopen undoes it on the next flush
        with self.pg.transaction(), self.pg.cursor(row_factory=dict_row) as cur:
            # the lock re-checks cond, so two replicas resolve once and a flush in between keeps the alert; id order as in flush
            cur.execute(
                f"WITH q AS (SELECT id FROM alerts WHERE id = ANY(%(ids)s) AND {cond} ORDER BY id FOR UPDATE)"
                " UPDATE alerts SET status = 'resolved', resolved_by = 'auto' FROM q WHERE alerts.id = q.id RETURNING alerts.*",
                {**quiet, "ids": ids})
            resolved = cur.fetchall()
            for row in resolved:
                self._change(cur, row, "resolved", now, actor="auto")
        return len(resolved)

    # --- acks and resolves made by people ---

    def apply_changes(self, records) -> int:
        """Apply `acked` / `resolved` records a person made through the api (events.md §4); return how many alerts changed.
        No outbox row and no ui.alerts message: the api already published the change, republishing it would loop."""
        # shortcut: idempotent by alert status, not change_id: a record redelivered or replayed after the alert reopened applies again, keep applied change_ids if that bites
        changed = 0
        for rec in records:
            alert_id, actor = rec["alert"]["id"], rec["actor"]
            if rec["change"] == "acked":  # counting goes on; acked_by stays until a reopen clears it
                changed += self.pg.execute("UPDATE alerts SET status = 'acked', acked_by = %s WHERE id = %s AND status = 'open'",
                                           (actor, alert_id)).rowcount
                continue
            if (row := self.pg.execute("SELECT alert_key FROM alerts WHERE id = %s AND status <> 'resolved'",
                                       (alert_id,)).fetchone()) is None:
                continue  # unknown, or already resolved
            key = row[0]
            self.flush([key])  # counts of events before the resolve land on this alert, not on a reopen
            changed += self.pg.execute("UPDATE alerts SET status = 'resolved', resolved_by = %s WHERE id = %s AND status <> 'resolved'",
                                       (actor, alert_id)).rowcount
            # shortcut: events counted in Redis between that flush and this drop are lost from the count (milliseconds), read-and-delete the hash atomically if exact counts matter
            # the next event for the key takes the miss path: reopens this alert inside 2 * window_s, opens a new one after
            self._exec(lambda pipe: pipe.evalsha(self._sha["drop"], 2, _hash_key(key), DIRTY, key, alert_id))
        return changed

    # --- unmapped schemas ---

    def save_samples(self, envelopes) -> None:
        """Per new unmapped schema: one schema_samples row and one `schema.samples` outbox record; a burst of one schema costs one insert."""
        new = {}
        for env in envelopes:
            if not env["mapped"] and (fp := fingerprint(env["source"], env["raw"])) not in self._seen:
                new.setdefault(fp, env)
        if not new:
            return
        with self.pg.transaction(), self.pg.cursor() as cur:
            # the outbox row is written only when this replica inserted the fingerprint
            cur.executemany(
                "WITH s AS (INSERT INTO schema_samples (fingerprint, source_id, created_at) VALUES (%s, %s, %s)"
                " ON CONFLICT DO NOTHING RETURNING fingerprint)"
                " INSERT INTO outbox (topic, key, payload) SELECT %s, fingerprint, %s FROM s",
                [(fp, env["source"], env["received_at"], SAMPLES_TOPIC,
                  Jsonb({"fingerprint": fp, "source_id": env["source"], "sample": _capped_raw(env["raw"]), "seen_at": env["received_at"]}))
                 for fp, env in new.items()])
        self._seen.update(new)
