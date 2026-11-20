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
            for i in first.values():
                row, counted = self._open(todo[i])
                seeds.append((row, todo[i]))
                if not counted:
                    again.add(i)
            todo = [it for i, it in enumerate(todo) if i in again]  # keeps partition order for the watermarks
        if raised:
            self.flush(raised)  # a severity raise should not wait for the next flush tick

    def _open(self, it: Item) -> tuple[dict, bool]:
        """Miss path: find, reopen or insert the alert. Returns the row and whether this event is already in its count."""
        env, m = it.envelope, it.match
        received = datetime.fromisoformat(env["received_at"])
        for _ in range(3):
            try:
                with self.pg.transaction(), self.pg.cursor(row_factory=dict_row) as cur:
                    cur.execute("SELECT * FROM alerts WHERE alert_key = %s AND status <> 'resolved'", (m.alert_key,))
                    if row := cur.fetchone():  # the hash expired or Redis lost it; a redelivered opener is already counted
                        return row, row["opened_by_event"] == env["event_id"]
                    cur.execute(
                        "SELECT * FROM alerts WHERE alert_key = %s AND status = 'resolved'"
                        " ORDER BY last_received_at DESC LIMIT 1 FOR UPDATE", (m.alert_key,))
                    old = cur.fetchone()
                    if old and old["opened_by_event"] == env["event_id"]:
                        return old, True  # a redelivered opener of an alert resolved since: already applied
                    # rules.md §4: auto-resolve after window_s of silence, reopen within another window_s
                    if old and received - old["last_received_at"] < timedelta(seconds=2 * it.window_s):
                        change = "reopened"
                        cur.execute(
                            "UPDATE alerts SET status = 'open', acked_by = NULL, resolved_by = NULL, count = count + 1,"
                            " severity = %s, last_seen = GREATEST(last_seen, %s),"
                            " last_received_at = GREATEST(last_received_at, %s), opened_by_event = %s,"
                            " opened_partition = %s, opened_offset = %s WHERE id = %s RETURNING *",
                            (max(old["severity"], env["severity"], key=SEV_RANK.get), env["ts"], received,
                             env["event_id"], it.partition, it.offset, old["id"]))
                    else:
                        change = "opened"
                        cur.execute(
                            "INSERT INTO alerts (id, alert_key, rule_id, team_id, status, severity, title, body, fields,"
                            " color, count, first_seen, last_seen, first_received_at, last_received_at, runbook_id,"
                            " sample_event, opened_by_event, opened_partition, opened_offset)"
                            " VALUES (%s, %s, %s, %s, 'open', %s, %s, %s, %s, %s, 1, %s, %s, %s, %s, %s, %s, %s, %s, %s)"
                            " RETURNING *",
                            (str(uuid.uuid4()), m.alert_key, m.rule_id, m.team_id, env["severity"],
                             _cut(m.title, TITLE_MAX), _cut(m.body, BODY_MAX),
                             Jsonb({k: _capped_field(v) for k, v in m.fields.items()}), m.color, env["ts"], env["ts"],
                             received, received, it.runbook_id, Jsonb({**env, "raw": _capped_raw(env["raw"])}),
                             env["event_id"], it.partition, it.offset))
                    row = cur.fetchone()
                    self._change(cur, row, change, received)
                    if row["count"] in THRESHOLDS:  # a reopen adds 1, so it crosses a threshold only by landing on it
                        self._change(cur, row, "count_threshold", received)
                    return row, True
            except psycopg.errors.UniqueViolation:
                continue  # another replica opened it first; the next round finds its row
        raise RuntimeError(f"could not open an alert for {m.alert_key}")

    @staticmethod
    def _change(cur, row: dict, change: str, at: datetime) -> None:
        """Outbox change record (events.md §4); the API builds the timeline from these."""
        record = {"change_id": str(uuid.uuid4()), "change": change, "at": _iso(at), "actor": "processor",
                  "alert": alert_object(row)}
        cur.execute("INSERT INTO outbox (topic, key, payload) VALUES (%s, %s, %s)", (TOPIC, row["id"], Jsonb(record)))

    # --- flush ---

    def flush(self, keys=None) -> None:
        """Bring Postgres up to date with the Redis counters of dirty alerts. Call about once per second."""
        keys = list(keys) if keys is not None else self.r.zrange(DIRTY, 0, -1)
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
