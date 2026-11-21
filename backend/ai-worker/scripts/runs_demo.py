"""Demo of the run judges (issue #75) against the stub api: a live risk check per `auto` run, then a start-mode proposal.

    uv run python scripts/runs_demo.py      (KAFKA_BROKERS default localhost:9092, API_URL default http://localhost:8000/api)

Needs the ai-worker running with TYPESAFE_API_KEY. It publishes two rules on `config.changes`, schedules three `auto`
runs in the stub (a staging retry, a large prod settlement retry, and one whose window is already over, so nobody can
judge it), prints each verdict and status, then adds 10 finished runs that a person approved within seconds and
prints the `runbook_start` proposal the ai-worker files for the staging rule. Each run uses new rule ids.
"""

import json
import os
import time
import urllib.error
import urllib.request
from datetime import UTC, datetime, timedelta

from confluent_kafka import Producer

BROKERS = os.environ.get("KAFKA_BROKERS", "localhost:9092")
API_URL = os.environ.get("API_URL", "http://localhost:8000/api").rstrip("/")
TAG = f"{datetime.now(UTC):%H%M%S}"
STAGING, PROD = f"demo-staging-retry-{TAG}", f"demo-prod-settlement-{TAG}"
WINDOW_S = 20


def iso(t: datetime) -> str:
    return t.isoformat(timespec="milliseconds").replace("+00:00", "Z")


def call(method: str, path: str, body=None, user: str = "demo"):
    req = urllib.request.Request(API_URL + path, method=method, headers={"X-User": user, "Content-Type": "application/json"},
                                 data=None if body is None else json.dumps(body).encode())
    try:
        with urllib.request.urlopen(req, timeout=10) as r:
            return r.status, json.loads(r.read())
    except urllib.error.HTTPError as e:
        return e.code, json.loads(e.read())


def rule(rid: str, name: str, env: str) -> dict:
    return {"id": rid, "team_id": "settlements", "name": name, "enabled": True, "runbook_id": "settlement-timeout",
            "when": {"all": [{"field": "type", "op": "eq", "value": "WORKFLOW_FAILED"},
                             {"field": "env", "op": "eq", "value": env}]}}


def run(rid: str, rule_id: str, workflow: dict, alert: dict, window_s: int) -> dict:
    return {"id": rid, "alert_id": f"alert-{rid}", "rule_id": rule_id, "team_id": "settlements",
            "runbook_id": "workflow-retry" if rule_id == STAGING else "settlement-timeout", "window_s": window_s,
            "alert": alert,
            "nodes": [{"id": "retry", "action": "retry_workflow", "risk": "auto", "params": workflow,
                       "text": "Retry the failed workflow"}]}


def alert(title: str, severity: str, service: str, env: str) -> dict:
    return {"title": title, "severity": severity, "rule_id": None,
            "sample_event": {"type": "WORKFLOW_FAILED", "service": service, "env": env, "severity": severity}}


def main() -> None:
    p = Producer({"bootstrap.servers": BROKERS})
    for r in (rule(STAGING, "Nightly report failed in staging", "staging"),
              rule(PROD, "Order settlement failed in prod", "prod")):
        p.produce("config.changes", key=f"rule:{r['id']}", value=json.dumps(
            {"kind": "rule", "id": r["id"], "updated_at": iso(datetime.now(UTC)), "deleted": False, "body": r}))
    if p.flush(10):
        raise SystemExit(f"config.changes records not delivered to {BROKERS}")
    print(f"rules {STAGING} and {PROD} published")
    time.sleep(3)  # the worker reads config.changes between jobs

    runs = [run(f"stg-{TAG}", STAGING, {"workflowId": "WF-STG-0042", "workflowName": "nightly-report"},
                alert("nightly-report failed in staging", "warning", "reporting", "staging"), WINDOW_S),
            run(f"prod-{TAG}", PROD, {"workflowId": "WF-20261121-0042", "workflowName": "order-settlement",
                                      "amount": 25000000, "currency": "EUR", "counterparty": "BANK-A"},
                alert("order-settlement failed: 25,000,000 EUR settlement timed out", "critical", "order-settlement",
                      "prod"), WINDOW_S),
            run(f"late-{TAG}", PROD, {"workflowId": "WF-20261121-0043", "workflowName": "order-settlement"},
                alert("order-settlement failed at step settle", "critical", "order-settlement", "prod"), 0)]
    for r in runs:
        call("POST", "/stub/runs", r)
    print(f"3 auto runs scheduled ({WINDOW_S} s window; the third one's window is already over)")
    deadline = time.monotonic() + WINDOW_S
    while time.monotonic() < deadline and any(call("GET", f"/runs/{r['id']}")[1]["risk_check"] is None
                                              for r in runs[:2]):
        time.sleep(1)
    for r in runs[:2]:
        rc = call("GET", f"/runs/{r['id']}")[1]["risk_check"] or {}
        print(f"{r['id']}: verdict {rc.get('verdict')} confidence {rc.get('confidence')} reasons {rc.get('reasons')}")
    time.sleep(max(0.0, deadline - time.monotonic()) + 1)
    for r in runs:
        got = call("GET", f"/runs/{r['id']}")[1]
        print(f"{r['id']} after its window: {got['status']}" + (f" ({got['hold_reasons']})" if got.get("hold_reasons")
                                                                else ""))
    held = next((r for r in runs if call("GET", f"/runs/{r['id']}")[1]["status"] == "waiting_approval"), None)
    if held:
        path = f"/runs/{held['id']}/approve"
        print(f"approve {held['id']} as ai-worker: {call('POST', path, user='ai-worker')[0]}; "
              f"as marek: {call('POST', path, user='marek')[1]['status']}")

    base = datetime.now(UTC) - timedelta(minutes=10)
    finished = []
    for i in range(10):  # staging retries, each approved by a person 4 to 12 s after it was ready, none failed
        t = base + timedelta(seconds=50 * i)
        finished.append({"id": f"done-{TAG}-{i}", "alert_id": f"alert-done-{TAG}-{i}", "rule_id": STAGING,
                         "team_id": "settlements", "runbook_id": "workflow-retry", "risk": "approval",
                         "trigger": "manual", "status": "done", "start_at": iso(t), "approved_by": "marek",
                         "approved_at": iso(t + timedelta(seconds=4 + i % 9)),
                         "started_at": iso(t + timedelta(seconds=4 + i % 9)), "finished_at": iso(t + timedelta(seconds=40)),
                         "cancelled_by": None, "risk_check": None,
                         "nodes": [{"id": "retry", "action": "retry_workflow", "risk": "auto", "status": "succeeded",
                                    "params": {"workflowId": f"WF-{i}"}}]})
    call("POST", "/stub/finished-runs", {"runs": finished})
    print("10 finished staging runs added, all approved by a person within seconds; waiting for a runbook_start "
          "proposal")
    mine = []
    for _ in range(45):  # the worker copies finished runs every 30 s
        mine = [x for x in call("GET", "/proposals")[1]
                if x.get("kind") == "runbook_start" and (x.get("body") or {}).get("rule_id") == STAGING]
        if mine:
            break
        time.sleep(2)
    print(json.dumps(mine, indent=1) if mine else "no runbook_start proposal (the judge agreed with today, or the "
                                                   "worker has not polled yet: see the jobs table)")


if __name__ == "__main__":
    main()
