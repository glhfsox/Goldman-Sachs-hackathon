#!/usr/bin/env python3
"""Fill a local API (backend/api's docker compose) with demo data for the UI.

Alerts, notification entries and new event shapes go on the API's Kafka the way the processor, the notifier
and the ai-worker publish them (contracts/events.md); rules, runbooks and proposals go through the REST API
(contracts/api.md). Safe to run again: every run adds fresh alerts; existing rules and runbooks are kept.

    python3 frontend/dev/seed.py                 # API on http://localhost:8000
    API_URL=http://localhost:8000 python3 frontend/dev/seed.py
"""
import json
import os
import subprocess
import sys
import time
import urllib.error
import urllib.request
from datetime import datetime, timedelta, timezone
from pathlib import Path

API = os.environ.get("API_URL", "http://localhost:8000").rstrip("/") + "/api"
COMPOSE = Path(__file__).resolve().parents[2] / "backend" / "api" / "docker-compose.yml"
RUN = str(int(time.time()))


def iso(delta_s: float = 0) -> str:
    t = datetime.now(timezone.utc) + timedelta(seconds=delta_s)
    return t.strftime("%Y-%m-%dT%H:%M:%S.") + f"{t.microsecond // 1000:03d}Z"


def call(method: str, path: str, body=None, user: str = "boris", ok=(200, 201, 409)):
    data = None if body is None else json.dumps(body).encode()
    req = urllib.request.Request(API + path, data=data, method=method,
                                 headers={"Content-Type": "application/json", "X-User": user})
    try:
        with urllib.request.urlopen(req) as r:
            text = r.read().decode()
            return json.loads(text) if text else None
    except urllib.error.HTTPError as e:
        if e.code in ok:
            return None
        sys.exit(f"{method} {path} -> {e.code}: {e.read().decode()[:400]}")


def produce(topic: str, records: list) -> None:
    lines = "".join(f"{key}|{json.dumps(value)}\n" for key, value in records)
    subprocess.run(["docker", "compose", "-f", str(COMPOSE), "exec", "-T", "kafka", "/opt/kafka/bin/kafka-console-producer.sh",
                    "--bootstrap-server", "kafka:9092", "--topic", topic,
                    "--reader-property", "parse.key=true", "--reader-property", "key.separator=|"],
                   input=lines.encode(), check=True, stdout=subprocess.DEVNULL)


def rule(rid, team, name, when, route, **extra):
    body = {"id": rid, "team_id": team, "name": name, "enabled": True, "when": when, "route": route, **extra}
    call("POST", "/rules", body)


def runbook(team, title, fmt, source, steps, accept_as=None):
    rb = call("POST", "/runbooks", {"team_id": team, "title": title, "format": fmt, "source": source})
    rid = rb["id"] if rb else title.lower().replace(" ", "-")
    if steps is not None:
        saved = call("PUT", f"/runbooks/{rid}", {"canonical": {"id": rid, "title": title, "steps": steps}, "status": "active"}, user="ai-worker")
        if accept_as and saved:
            call("POST", f"/runbooks/{rid}/accept?version={saved['version']}", user=accept_as)
    return rid


def main() -> None:
    print("rules and runbooks…")
    st = runbook("settlements", "Settlement timeout", "yaml",
                 'steps:\n  - check: "Is counterparty-service healthy?"\n    action: http_get\n  - action: retry_workflow\n    workflowId: "{{workflowId}}"\n  - if: "retry fails"\n    action: escalate\n    team: counterparty-support',
                 [{"text": "Check counterparty-service health", "action": "http_get", "params": {"url": "http://counterparty-service/health"}},
                  {"text": "Retry workflow {{raw.workflowId}}", "action": "retry_workflow", "params": {"workflowId": "{{raw.workflowId}}"}},
                  {"text": "Escalate to counterparty-support", "when": "Only if the retry fails", "action": "escalate", "params": {"team": "counterparty-support"}},
                  {"text": "Post an update in #settlements-incidents"}],
                 accept_as="boris")
    wr = runbook("settlements", "Workflow retry", "markdown", "# Retry a failed workflow\n1. Retry the workflow from the failed step.",
                 [{"text": "Retry workflow {{raw.workflowId}}", "action": "retry_workflow", "params": {"workflowId": "{{raw.workflowId}}"}}],
                 accept_as="boris")
    runbook("pricing", "Pricing latency", "text",
            "When pricing latency goes over a second, it's usually the cache. Restart the pricing-engine instance named in the alert, wait 2 minutes, and if latency is still high, call the pricing on-call person.",
            [{"text": "Restart pricing-engine instance {{service}}", "action": "restart_service", "params": {"service": "{{service}}"}},
             {"text": "Wait 2 minutes, then check latency on the alert"},
             {"text": "If latency is still above 1 s, escalate to the Pricing on-call person", "action": "escalate", "params": {"team": "pricing"}}])
    runbook("settlements", "Mainframe batch notes", "text", "If EOD-RECON abends, kick the batch from the console and tell the night operator.", None)

    rule("settlements-workflow-failed", "settlements", "Order settlement failed in prod",
         {"all": [{"field": "type", "op": "eq", "value": "WORKFLOW_FAILED"}, {"field": "raw.workflowName", "op": "eq", "value": "order-settlement"}, {"field": "env", "op": "eq", "value": "prod"}]},
         {"recipients": [{"team": "settlements", "on_call": True}], "channels": ["call", "sms", "ui"]},
         severity="critical", alert_key=["raw.workflowName", "raw.error.code"], window_s=300,
         present={"title": "{{workflowName}} failed at step {{step}}", "body": "{{error.message}}", "color": "red"}, runbook_id=st)
    rule("settlements-staging", "settlements", "Any workflow failure in staging",
         {"all": [{"field": "type", "op": "eq", "value": "WORKFLOW_FAILED"}, {"field": "env", "op": "eq", "value": "staging"}]},
         {"recipients": [{"team": "settlements"}]}, alert_key=["raw.workflowName"], window_s=1800,
         present={"title": "{{workflowName}} failed in staging", "color": "orange"}, runbook_id=wr)
    rule("pricing-engine-errors", "pricing", "Pricing engine errors",
         {"all": [{"field": "service", "op": "glob", "value": "pricing-engine*"}, {"field": "severity", "op": "sev_gte", "value": "error"}]},
         {"recipients": [{"team": "pricing"}], "channels": ["ui"]}, alert_key=["raw.instrument"], present={"title": "Pricing errors on {{instrument}}", "color": "orange"})
    rule("rates-large-rejections", "rates", "Large rejected rates trades",
         {"all": [{"field": "type", "op": "glob", "value": "trade.rejected*"}, {"field": "payload.desk", "op": "eq", "value": "rates"}, {"field": "payload.notional", "op": "gt", "value": 10000000}]},
         {"recipients": [{"team": "rates"}], "channels": ["email", "ui"]},
         present={"title": "Trade {{payload.tradeId}} rejected: {{payload.reason}} ({{payload.notional}} {{payload.currency}})", "color": "orange"})
    rule("anna-payments-critical", "payments", "Anything critical from payments-api",
         {"all": [{"field": "service", "op": "eq", "value": "payments-api"}, {"field": "severity", "op": "eq", "value": "critical"}]},
         {"recipients": [{"user": "anna"}]}, severity="critical")

    print("alerts on Kafka…")
    def alert(n, team, rule_id, sev, status, title, body, count, first_ago, fields, runbook_id=None, acked_by=None, resolved_by=None, sample=None):
        aid = f"demo-{RUN}-{n}"
        return aid, {"id": aid, "rule_id": rule_id, "team_id": team, "incident_id": None, "status": status, "severity": sev, "title": title, "body": body,
                     "fields": fields, "color": None, "count": count, "first_seen": iso(-first_ago), "last_seen": iso(-2),
                     "first_received_at": iso(-first_ago), "runbook_id": runbook_id, "acked_by": acked_by, "resolved_by": resolved_by,
                     "sample_event": sample or {"type": "WORKFLOW_FAILED"}}

    alerts = [
        alert(1, "settlements", "settlements-workflow-failed", "critical", "open", "order-settlement failed at step settle",
              "Counterparty service did not respond in 5000 ms", 512, 120, {"step": "settle", "error.code": "TIMEOUT", "environment": "prod"}, st,
              sample={"type": "WORKFLOW_FAILED", "raw": {"workflowId": "WF-20261120-0042"}}),
        alert(2, "settlements", "settlements-staging", "warning", "open", "trade-booking failed in staging",
              "Booking service did not respond in 3000 ms", 2, 8, {"step": "enrich", "environment": "staging"}, wr,
              sample={"type": "WORKFLOW_FAILED", "raw": {"workflowId": "WF-20261120-0311"}}),
        alert(3, "payments", "anna-payments-critical", "critical", "acked", "Error rate 12% on payments-api (eu-west)",
              "Error rate 12% over the last 5 minutes", 15, 600, {"service": "payments-api", "region": "eu-west"}, acked_by="anna"),
        alert(4, "pricing", "pricing-engine-errors", "error", "open", "Pricing errors on EURUSD",
              "Price calculation exceeded 1000 ms: cache miss", 94, 300, {"instrument": "EURUSD", "instance": "pricing-engine-3"}),
        alert(5, "rates", "rates-large-rejections", "error", "open", "Trade T-88121 rejected: LIMIT_BREACH (25000000 EUR)",
              "Rejected by trade-gateway on the rates desk", 1, 200, {"desk": "rates", "notional": 25000000}),
        alert(6, "settlements", "settlements-staging", "info", "resolved", "eod-reconciliation slow in staging",
              "Closed by itself: no repeat for 30 minutes", 3, 7200, {"environment": "staging"}, resolved_by="auto"),
    ]
    produce("alerts.changes", [(aid, {"change_id": f"{aid}-opened", "change": "opened", "at": iso(), "actor": "processor", "alert": a}) for aid, a in alerts])

    a1 = alerts[0][0]
    produce("alerts.timeline", [
        (a1, {"entry_id": f"{a1}-n1", "alert_id": a1, "at": iso(-118), "actor": "notifier", "kind": "notification_sent",
              "data": {"notification": {"id": f"{a1}-call", "user_id": "boris", "channel": "call", "status": "sent",
                                        "reason": "rule Order settlement failed in prod, on-call", "send_at": iso(-119), "sent_at": iso(-118), "error": None}}}),
        (a1, {"entry_id": f"{a1}-n2", "alert_id": a1, "at": iso(-117), "actor": "notifier", "kind": "notification_suppressed",
              "data": {"notification": {"id": f"{a1}-sms2", "user_id": "boris", "channel": "sms", "status": "suppressed",
                                        "reason": "511 repeats grouped into this alert", "send_at": iso(-117), "sent_at": None, "error": None}}}),
    ])

    print("new event shapes…")
    fp_k8s, fp_batch = f"demo-k8s-{RUN}", f"demo-batch-{RUN}"
    produce("schema.samples", [
        (fp_k8s, {"fingerprint": fp_k8s, "source_id": "alertmanager-http", "seen_at": iso(),
                  "sample": {"apiVersion": "v1", "kind": "Event", "involvedObject": {"kind": "Pod", "name": "pricing-engine-3-7c9f4-b2k8x"},
                             "reason": "BackOff", "message": "Back-off restarting failed container", "type": "Warning", "lastTimestamp": "2026-11-20T10:23:41Z"}}),
        (fp_batch, {"fingerprint": fp_batch, "source_id": "pricing-logs-http", "seen_at": iso(),
                    "sample": {"job": "EOD-RECON-7", "host": "mf01", "rc": 12, "msg": "ABEND S0C7 in step RECON3", "at": "2026-11-20T10:31:05Z"}}),
    ])

    time.sleep(3)  # let the api consume the records before filing proposals on the alerts
    print("proposals…")
    call("PUT", f"/schema-samples/{fp_batch}", {"status": "failed", "error": "no field looks like a severity"}, user="ai-worker", ok=(200, 404, 409))
    call("POST", f"/alerts/{alerts[1][0]}/proposals", {"kind": "rule_severity", "body": {
        "rule_id": "settlements-staging", "team_id": "settlements", "severity": "warning", "current_severity": None, "confidence": 0.82,
        "reasons": ["people acknowledged only 10% of its last 20 alerts", "most closed by themselves within 5 minutes", "none was reopened"]}},
        user="ai-worker", ok=(200, 201, 404))
    call("POST", f"/alerts/{a1}/proposals", {"kind": "threshold", "body": {
        "team_id": "settlements", "rule_id": "settlements-workflow-failed", "text": "Wait for 3 failures in 1 minute before calling on-call; a single timeout usually recovers on retry."}},
        user="elena", ok=(200, 201, 404))
    print(f"done. Open http://localhost:3000 and sign in as Boris Nowak (Settlements, on call).")


if __name__ == "__main__":
    main()
