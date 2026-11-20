"""The processor's copy of mappings, rules and the service map, fed by Kafka `config.changes` (events.md §6)."""

import logging
from datetime import datetime

import psycopg
from psycopg.types.json import Jsonb

from .core import compile_mappings, compile_rules

log = logging.getLogger(__name__)

# Newer-or-unseen records win; an equal or older `updated_at` changes nothing, so replays are no-ops.
_UPSERT = {
    "mapping": "INSERT INTO mappings (id, status, priority, body, updated_at) VALUES (%s, %s, %s, %s, %s)"
               " ON CONFLICT (id) DO UPDATE SET status = EXCLUDED.status, priority = EXCLUDED.priority,"
               " body = EXCLUDED.body, updated_at = EXCLUDED.updated_at WHERE mappings.updated_at < EXCLUDED.updated_at",
    "rule": "INSERT INTO rules (id, team_id, enabled, body, updated_at) VALUES (%s, %s, %s, %s, %s)"
            " ON CONFLICT (id) DO UPDATE SET team_id = EXCLUDED.team_id, enabled = EXCLUDED.enabled,"
            " body = EXCLUDED.body, updated_at = EXCLUDED.updated_at WHERE rules.updated_at < EXCLUDED.updated_at",
}
# shortcut: a delete leaves no tombstone, so replaying an older upsert after it brings the row back; keep a deleted_at column if mappings or rules ever get hard-deleted
_DELETE = {
    "mapping": "DELETE FROM mappings WHERE id = %s AND updated_at <= %s",
    "rule": "DELETE FROM rules WHERE id = %s AND updated_at <= %s",
}
# Any insert, update or delete changes this; the tables hold tens of rows.
# shortcut: scans every (id, updated_at), switch to a version counter if the tables grow past ~10k rows
_STAMP = ("SELECT (SELECT md5(string_agg(id || updated_at, ',' ORDER BY id)) FROM mappings),"
          " (SELECT md5(string_agg(id || updated_at, ',' ORDER BY id)) FROM rules)")


def _typed(v, t):
    return v if type(v) is t else None  # the column is informational; a bad type is caught by compile_* on the body


class Config:
    """Compiled mappings and rules for one replica; every replica sees every replica's `apply` on its next `refresh`."""

    def __init__(self, conn):
        self.pg = conn
        self.mappings, self.rules, self._stamp = [], [], None
        self.refresh()

    def apply(self, records: list[dict]) -> None:
        """Store decoded `config.changes` records of kinds mapping, rule, service_dep; skip the rest. One transaction."""
        with self.pg.transaction(), self.pg.cursor() as cur:
            for r in records:
                try:
                    kind, rid, deleted = r["kind"], r["id"], r.get("deleted") is True
                    if kind not in ("mapping", "rule", "service_dep"):
                        continue
                    body = r["body"]
                    if type(rid) is not str or not (deleted or type(body) is dict):
                        raise ValueError("id must be a string and body an object")
                    if kind != "service_dep" and not deleted and body.get("id") != rid:
                        raise ValueError("body.id must equal id")  # else two rows compile to one rule or mapping id
                    with self.pg.transaction():  # savepoint: a value Postgres rejects skips only this record
                        if kind == "service_dep":
                            service, _, depends_on = rid.partition(":")
                            if deleted:
                                cur.execute("DELETE FROM service_deps WHERE service = %s AND depends_on = %s", (service, depends_on))
                            elif type(body.get("service")) is str and type(body.get("depends_on")) is str:
                                cur.execute("INSERT INTO service_deps (service, depends_on) VALUES (%s, %s) ON CONFLICT DO NOTHING",
                                            (body["service"], body["depends_on"]))
                            else:
                                raise ValueError("service_dep body needs string service and depends_on")
                            continue
                        at = datetime.fromisoformat(r["updated_at"])
                        if deleted:
                            cur.execute(_DELETE[kind], (rid, at))
                        elif kind == "mapping":
                            cur.execute(_UPSERT[kind], (rid, _typed(body.get("status"), str), _typed(body.get("priority"), int), Jsonb(body), at))
                        else:
                            cur.execute(_UPSERT[kind], (rid, _typed(body.get("team_id"), str), _typed(body.get("enabled", True), bool), Jsonb(body), at))
                # not OperationalError as a whole: a lost connection must end the process
                except (KeyError, TypeError, ValueError, AttributeError, RecursionError, psycopg.DataError,
                        psycopg.errors.ProgramLimitExceeded, psycopg.errors.StatementTooComplex) as e:
                    log.warning("skipping malformed config record %r: %s", r, e)

    def refresh(self) -> bool:
        """Recompile if the tables changed since the last load; True if the lists were swapped."""
        with self.pg.transaction():
            stamp = self.pg.execute(_STAMP).fetchone()
            if stamp == self._stamp:
                return False
            mappings = [row[0] for row in self.pg.execute("SELECT body FROM mappings")]
            rules = [row[0] for row in self.pg.execute("SELECT body FROM rules")]
        # compile_* keep only active mappings and enabled rules
        self.mappings, errors = compile_mappings(mappings)
        for rid, msg in errors:
            log.warning("invalid mapping %s: %s", rid, msg)
        self.rules, errors = compile_rules(rules)
        for rid, msg in errors:
            log.warning("invalid rule %s: %s", rid, msg)
        self._stamp = stamp
        return True
