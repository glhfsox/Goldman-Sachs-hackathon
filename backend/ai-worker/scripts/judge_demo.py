"""Demo of the severity judge: a new rule, its first alert, then 20 alerts that people acked and resolved.

    uv run python scripts/judge_demo.py      (KAFKA_BROKERS default localhost:9092, API_URL default http://localhost:8000/api)

Start it once the ai-worker has logged "ai-worker started" (its `alerts.changes` group starts at the latest offset).
It creates the rule through the api (`POST /rules`, which publishes it on `config.changes`), publishes the alerts'
changes on `alerts.changes` the way the processor and the api would, then prints the `rule_severity` proposals the
api received for that rule. Works against the real api and the stub. Each run uses a new rule id.
"""

import json
import os
import time
import urllib.request
import uuid
from datetime import UTC, datetime, timedelta

from confluent_kafka import Producer

BROKERS = os.environ.get("KAFKA_BROKERS", "localhost:9092")
API_URL = os.environ.get("API_URL", "http://localhost:8000/api").rstrip("/")
RULE_ID = f"demo-settlement-{datetime.now(UTC):%H%M%S}"
RULE = {"id": RULE_ID, "team_id": "settlements", "name": "Order settlement failed in prod", "enabled": True,
        "when": {"all": [{"field": "type", "op": "eq", "value": "WORKFLOW_FAILED"},
                         {"field": "env", "op": "eq", "value": "prod"}]},
        "route": {"recipients": [{"team": "settlements", "on_call": True}], "channels": ["sms", "ui"]},
        "present": {"title": "{{workflowName}} failed at step {{step}}", "body": "{{error.message}}"},
        "severity": "info"}


def iso(t: datetime) -> str:
    return t.isoformat(timespec="milliseconds").replace("+00:00", "Z")


def alert(alert_id: str, opened: datetime, count: int) -> dict:
    return {"id": alert_id, "rule_id": RULE_ID, "team_id": "settlements", "incident_id": None, "status": "open",
            "severity": "critical", "color": None, "runbook_id": None, "acked_by": None, "resolved_by": None,
            "title": "order-settlement failed at step settle", "body": "Counterparty service did not respond in 5000 ms",
            "fields": {"step": "settle", "error.code": "TIMEOUT"}, "count": count, "first_seen": iso(opened),
            "last_seen": iso(opened), "first_received_at": iso(opened),
            "sample_event": {"type": "WORKFLOW_FAILED", "service": "order-settlement", "env": "prod",
                             "severity": "critical", "mapped": True}}


def api(method: str, path: str, body=None):
    req = urllib.request.Request(API_URL + path, method=method, data=body and json.dumps(body).encode(),
                                 headers={"Content-Type": "application/json", "X-User": "anna"})
    with urllib.request.urlopen(req, timeout=10) as r:
        return json.loads(r.read())


def main() -> None:
    p = Producer({"bootstrap.servers": BROKERS})

    def send(topic, key, value):
        p.produce(topic, key=key, value=json.dumps(value))
        if p.flush(10):
            raise SystemExit(f"{topic} record not delivered to {BROKERS}")

    def change(kind, a, at, actor):
        send("alerts.changes", a["id"], {"change_id": str(uuid.uuid4()), "change": kind, "at": iso(at),
                                         "actor": actor, "alert": a})

    api("POST", "/rules", RULE)
    print(f"rule {RULE_ID} created (severity {RULE['severity']})")
    time.sleep(3)  # the worker reads config.changes between jobs
    base = datetime.now(UTC) - timedelta(hours=3)
    ids = [str(uuid.uuid4()) for _ in range(20)]
    change("opened", alert(ids[0], base, 1), base, "processor")  # the rule's first alert: a `first` job
    print("first alert opened")
    time.sleep(3)  # let it be read before any resolve on another partition
    for i, aid in enumerate(ids):
        opened = base + timedelta(minutes=5 * i)
        if i:
            change("opened", alert(aid, opened, 1), opened, "processor")
        if i % 4:  # 15 of 20 acked by a person after 2 to 4 minutes
            a = alert(aid, opened, 1)
            change("acked", a | {"status": "acked", "acked_by": "anna"}, opened + timedelta(minutes=2 + i % 3), "anna")
            change("resolved", a | {"status": "resolved", "acked_by": "anna", "resolved_by": "anna"},
                   opened + timedelta(minutes=10), "anna")
        else:
            change("resolved", alert(aid, opened, 4) | {"status": "resolved"}, opened + timedelta(minutes=8), "auto")
    print("20 alerts acked / resolved; waiting for proposals")
    for _ in range(60):
        mine = [x for x in api("GET", "/proposals?limit=200")
                if x.get("kind") == "rule_severity" and (x.get("body") or {}).get("rule_id") == RULE_ID]
        if {x["body"]["mode"] for x in mine} >= {"first", "history"}:
            break
        time.sleep(2)
    print(json.dumps(mine, indent=1))


if __name__ == "__main__":
    main()
