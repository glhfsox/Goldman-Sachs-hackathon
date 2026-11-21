import copy
import glob
import os
import shutil
import socket
import subprocess
from pathlib import Path

import fakeredis
import psycopg
import pytest
import redis
from psycopg.rows import dict_row

from processor import compile_mappings, compile_rules

MIGRATIONS = Path(__file__).parent.parent / "migrations"

# Appendix A events with the brief's "..." filled in, plus a pricing log line for Team Pricing.
PAYLOADS = {
    "workflow": ("workflows-kafka", {
        "type": "WORKFLOW_FAILED", "workflowId": "WF-20261120-0042", "workflowName": "order-settlement",
        "step": "settle", "severity": "critical", "environment": "prod", "timestamp": "2026-11-20T10:15:01Z",
        "error": {"code": "TIMEOUT", "message": "Counterparty service did not respond in 5000 ms"},
    }),
    "metric": ("alertmanager-http", {
        "alertname": "HighErrorRate",
        "labels": {"service": "payments-api", "region": "eu-west", "severity": "critical"},
        "annotations": {"summary": "Error rate 12% over the last 5 minutes"},
        "startsAt": "2026-11-20T11:14:00.250+01:00", "status": "firing",
    }),
    "trade": ("trades-kafka", {
        "meta": {"schema": "trade.rejected.v1", "producer": "trade-gateway", "ts": 1795169701123},
        "payload": {"tradeId": "T-88231", "desk": "rates", "notional": 25000000,
                    "currency": "EUR", "reason": "LIMIT_BREACH"},
    }),
    "pricing": ("pricing-logs", {
        "logger": "pricing-engine-eu-1", "level": "ERR", "instrument": "EURUSD",
        "msg": "quote latency 1450 ms", "time": 1795169701,
    }),
}

SEED_MAPPINGS = [
    {"id": "workflow-failed", "status": "active", "origin": "seed", "priority": 100,
     "match": {"all": [{"field": "raw.type", "op": "eq", "value": "WORKFLOW_FAILED"}]},
     "fields": {"type": {"path": "type"}, "service": {"path": "workflowName"},
                "severity": {"path": "severity"}, "env": {"path": "environment"},
                "ts": {"path": "timestamp", "format": "iso"}},
     "dedup_fields": ["workflowName", "error.code"]},
    {"id": "alertmanager", "status": "active", "origin": "seed", "priority": 100,
     "match": {"all": [{"field": "source", "op": "eq", "value": "alertmanager-http"},
                       {"field": "raw.alertname", "op": "exists"}]},
     "fields": {"type": {"path": "alertname"}, "service": {"path": "labels.service"},
                "severity": {"path": "labels.severity"}, "env": {"const": "prod"},
                "ts": {"path": "startsAt", "format": "iso"}},
     "dedup_fields": ["labels.region"]},
    # verbatim from contracts/rules.md §3
    {"id": "trade-rejected-v1", "status": "active", "origin": "llm", "priority": 100,
     "match": {"all": [{"field": "raw.meta.schema", "op": "glob", "value": "trade.rejected*"}]},
     "fields": {"type": {"path": "meta.schema"}, "service": {"path": "meta.producer"},
                "severity": {"const": "error"}, "env": {"const": "prod"},
                "ts": {"path": "meta.ts", "format": "epoch_ms"}},
     "severity_map": {"P1": "critical", "ERROR": "error", "WARN": "warning"},
     "dedup_fields": ["payload.desk", "payload.reason"]},
    {"id": "pricing-logs", "status": "active", "origin": "seed", "priority": 50,
     "match": {"all": [{"field": "raw.logger", "op": "glob", "value": "pricing-engine*"}]},
     "fields": {"type": {"const": "log"}, "service": {"path": "logger"}, "severity": {"path": "level"},
                "env": {"const": "prod"}, "ts": {"path": "time", "format": "epoch_s"}},
     "severity_map": {"ERR": "error", "FATAL": "critical"}},
]

# contracts/rules.md §4, verbatim
EXAMPLE_RULES = [
    {"id": "settlements-workflow-failed", "team_id": "settlements",
     "name": "Order settlement failed in prod", "enabled": True,
     "when": {"all": [{"field": "type", "op": "eq", "value": "WORKFLOW_FAILED"},
                      {"field": "raw.workflowName", "op": "eq", "value": "order-settlement"},
                      {"field": "env", "op": "eq", "value": "prod"}]},
     "alert_key": ["raw.workflowName", "raw.error.code"], "window_s": 300,
     "route": {"recipients": [{"team": "settlements", "on_call": True}], "channels": ["call", "sms", "ui"]},
     "present": {"title": "{{workflowName}} failed at step {{step}}", "body": "{{error.message}}",
                 "fields": ["step", "error.code", "error.message"], "color": "red"},
     "runbook_id": "settlement-timeout"},
    {"id": "pricing-engine-errors", "team_id": "pricing",
     "when": {"all": [{"field": "service", "op": "glob", "value": "pricing-engine*"},
                      {"field": "severity", "op": "sev_gte", "value": "error"}]},
     "alert_key": ["raw.instrument"], "window_s": 300,
     "route": {"recipients": [{"team": "pricing"}], "channels": ["ui"]},
     "present": {"title": "Pricing errors on {{instrument}}", "color": "orange"}},
    {"id": "rates-large-rejections", "team_id": "rates",
     "when": {"all": [{"field": "type", "op": "glob", "value": "trade.rejected*"},
                      {"field": "payload.desk", "op": "eq", "value": "rates"},
                      {"field": "payload.notional", "op": "gt", "value": 10000000}]},
     "route": {"recipients": [{"team": "rates"}], "channels": ["email", "ui"]},
     "present": {"title": "Trade {{payload.tradeId}} rejected: {{payload.reason}} "
                          "({{payload.notional}} {{payload.currency}})"}},
    {"id": "anna-payments-critical", "team_id": "payments",
     "when": {"all": [{"field": "service", "op": "eq", "value": "payments-api"},
                      {"field": "severity", "op": "eq", "value": "critical"}]},
     "route": {"recipients": [{"user": "anna"}]}},
]


def make_event(source_id, payload, n=0):
    return {"event_id": f"evt-{n}", "source_id": source_id,
            "received_at": "2026-11-20T10:15:02.123Z", "payload": copy.deepcopy(payload)}


@pytest.fixture(scope="session")
def mappings():
    compiled, errors = compile_mappings(SEED_MAPPINGS)
    assert errors == []
    return compiled


@pytest.fixture(scope="session")
def rules():
    compiled, errors = compile_rules(EXAMPLE_RULES)
    assert errors == []
    return compiled


def _pg_bindir():
    for path in [None, *sorted(glob.glob("/usr/lib/postgresql/*/bin"), reverse=True)]:
        if (initdb := shutil.which("initdb", path=path)) and shutil.which("pg_ctl", path=os.path.dirname(initdb)):
            return os.path.dirname(initdb)


@pytest.fixture(scope="session")
def pg_url(tmp_path_factory):
    if url := os.environ.get("TEST_DATABASE_URL"):
        yield url
        return
    bindir = _pg_bindir()
    if bindir is None:
        pytest.skip("no PostgreSQL binaries (initdb, pg_ctl) on PATH or in /usr/lib/postgresql/*/bin")
    root = tmp_path_factory.mktemp("pg")
    data = root / "data"
    subprocess.run([f"{bindir}/initdb", "-D", data, "-U", "postgres", "-A", "trust", "-E", "UTF8", "--no-sync"],
                   check=True, capture_output=True)
    with socket.socket() as s:  # a free loopback port, never the default 5432
        s.bind(("127.0.0.1", 0))
        port = s.getsockname()[1]
    with open(data / "postgresql.conf", "a") as f:
        f.write(f"\nport = {port}\nlisten_addresses = '127.0.0.1'\nunix_socket_directories = ''\nfsync = off\n")
    subprocess.run([f"{bindir}/pg_ctl", "-D", data, "-l", root / "log", "-w", "start"], check=True, capture_output=True)
    try:
        yield f"postgresql://postgres@127.0.0.1:{port}/postgres"
    finally:
        subprocess.run([f"{bindir}/pg_ctl", "-D", data, "-m", "immediate", "stop"], capture_output=True)


@pytest.fixture(scope="session")
def db(pg_url):
    with psycopg.connect(pg_url, autocommit=True) as c:
        for f in sorted(MIGRATIONS.glob("*.sql")):
            c.execute(f.read_text())
    return pg_url


@pytest.fixture
def sql(db):
    with psycopg.connect(db, autocommit=True, row_factory=dict_row) as c:
        c.execute("TRUNCATE alerts, outbox, outbox_dead, schema_samples, parked_events RESTART IDENTITY")
        yield lambda q, *a: c.execute(q, a).fetchall()


@pytest.fixture
def new_redis():
    if url := os.environ.get("TEST_REDIS_URL"):
        def make():
            return redis.Redis.from_url(url, decode_responses=True)
    else:
        server = fakeredis.FakeServer()

        def make():
            return fakeredis.FakeRedis(server=server, decode_responses=True)
    make().flushdb()
    return make
