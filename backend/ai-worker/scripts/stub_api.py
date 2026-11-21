"""A temporary stand-in for the api service (contracts/api.md), just the endpoints the ai-worker calls.

    uv run python scripts/stub_api.py        (port 8000, Kafka from KAFKA_BROKERS, default localhost:9092)

State is in memory. Mappings and runbooks it saves are published to `config.changes` (events.md §6) the way the api
would, so the processor and the ai-worker see them. Remove it once backend/api serves these endpoints.

Runbook runs (issue #75, api.md "Runbook runs"): the reference behaviour of `GET /runs`, `POST /runs/{id}/risk-check`
and `POST /runs/{id}/approve`; a scheduled run with no verdict at `start_at` goes to `waiting_approval`. Stub-only
helpers for demos: `POST /api/stub/runs` (a scheduled `auto` run: the run's fields, `window_s`, optional `alert`)
and `POST /api/stub/finished-runs` (a list of finished runs, stored as given).
"""

import json
import os
import re
import sys
import uuid
from datetime import UTC, datetime, timedelta
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path
from urllib.parse import parse_qs, urlsplit

from confluent_kafka import Producer

BROKERS = os.environ.get("KAFKA_BROKERS", "localhost:9092")
PORT = int(os.environ.get("PORT", "8000"))
FIXTURE = (Path(__file__).parent / "alert_fixture.json").read_text()

mappings, samples, runbooks, proposals = {}, {}, {}, {}
runs, alerts = {}, {}  # run id -> RunbookRun; alert id -> Alert, for alerts the stub helpers brought
SERVICE_ACTORS = {"ai-worker", "notifier", "processor"}
producer = None


def now_iso(dt=None) -> str:
    return (dt or datetime.now(UTC)).isoformat(timespec="milliseconds").replace("+00:00", "Z")


def tick() -> None:
    """At `start_at` a scheduled run starts when it was cleared, else waits for a person (no verdict, or hold)."""
    now = now_iso()
    for r in runs.values():
        if r["status"] == "scheduled" and r["start_at"] <= now:
            cleared = (r["risk_check"] or {}).get("verdict") == "clear"
            r |= {"status": "running", "started_at": now} if cleared else \
                {"status": "waiting_approval", "hold_reasons": ["risk check did not answer"]}


def publish(kind: str, body: dict) -> None:
    """Same record shape and key as backend/processor/scripts/seed.py."""
    at = datetime.now(UTC).isoformat(timespec="milliseconds")[:-6] + "Z"
    record = {"kind": kind, "id": body["id"], "updated_at": at, "deleted": False, "body": body}
    producer.produce("config.changes", key=f"{kind}:{body['id']}", value=json.dumps(record))
    if producer.flush(10):
        raise RuntimeError("config.changes record not delivered")


class Handler(BaseHTTPRequestHandler):
    def _send(self, status: int, body) -> None:
        data = json.dumps(body).encode()
        self.send_response(status)
        self.send_header("Content-Type", "application/json")
        self.send_header("Content-Length", str(len(data)))
        self.end_headers()
        self.wfile.write(data)

    def _route(self, method: str) -> None:
        url = urlsplit(self.path)
        path, query = url.path, parse_qs(url.query)
        body = None
        if n := int(self.headers.get("Content-Length") or 0):
            try:
                body = json.loads(self.rfile.read(n))
            except ValueError:
                return self._send(400, {"error": "bad_json", "message": "body is not JSON", "details": []})
        sys.stderr.write(f"{method} {self.path} X-User={self.headers.get('X-User')} body={json.dumps(body)[:500]}\n")
        if method in ("PUT", "POST") and type(body) is not dict and not path.endswith("/approve"):
            return self._send(400, {"error": "bad_body", "message": "body must be a JSON object", "details": []})

        if method == "PUT" and (m := re.fullmatch(r"/api/mappings/([^/]+)", path)):
            if body.get("id") != m[1]:
                return self._send(422, {"error": "invalid_mapping", "message": "1 problem",
                                        "details": [{"path": "id", "message": "must equal the id in the path"}]})
            mappings[m[1]] = {**body, "fingerprint": query.get("fingerprint", [None])[0]}
            publish("mapping", body)
            return self._send(200, body)
        if method == "PUT" and (m := re.fullmatch(r"/api/schema-samples/([^/]+)", path)):
            samples[m[1]] = body
            return self._send(200, body)
        if method == "POST" and path == "/api/runbooks":
            rb = {"id": body.get("id") or f"rb-{uuid.uuid4().hex[:8]}", "team_id": body.get("team_id"),
                  "title": body.get("title"), "format": body.get("format"), "source": body.get("source"),
                  "canonical": None, "status": "draft", "version": 1}
            runbooks[rb["id"]] = rb
            publish("runbook", rb)
            return self._send(201, rb)
        if method == "GET" and path == "/api/runbooks":
            return self._send(200, list(runbooks.values()))
        if method == "PUT" and (m := re.fullmatch(r"/api/runbooks/([^/]+)", path)):
            if m[1] not in runbooks:
                return self._send(404, {"error": "not_found", "message": f"runbook {m[1]}", "details": []})
            rb = runbooks[m[1]] = {**runbooks[m[1]], **{k: body[k] for k in ("canonical", "status") if k in body}}
            publish("runbook", rb)
            return self._send(200, rb)
        if path.startswith("/api/runs") or path.startswith("/api/stub/"):
            return self._runs(method, path, query, body)
        if method == "GET" and (m := re.fullmatch(r"/api/alerts/([^/]+)", path)) and m[1] in alerts:
            return self._send(200, {"alert": alerts[m[1]], "incident": None, "timeline": [], "notifications": [],
                                    "runbook": None})
        if method == "GET" and (m := re.fullmatch(r"/api/alerts/([^/]+)", path)):
            doc = json.loads(FIXTURE.replace("ALERT_ID", m[1]))
            return self._send(200, {"alert": doc["alert"], "incident": None, "timeline": doc["timeline"],
                                    "notifications": [], "runbook": None})
        if method == "POST" and (m := re.fullmatch(r"/api/alerts/([^/]+)/proposals", path)):
            p = {"id": f"prop-{uuid.uuid4().hex[:8]}", "alert_id": m[1], "kind": body.get("kind"), "body": body.get("body"),
                 "author": self.headers.get("X-User") or "unknown", "status": "open", "decided_by": None}
            proposals[p["id"]] = p
            return self._send(201, p)
        if method == "GET" and path == "/api/proposals":
            return self._send(200, list(proposals.values()))
        return self._send(404, {"error": "not_found", "message": f"{method} {path}", "details": []})

    def _runs(self, method: str, path: str, query: dict, body):
        tick()
        actor = self.headers.get("X-User") or ""
        if method == "GET" and path == "/api/runs":
            if query.get("risk_check") == ["pending"]:
                return self._send(200, [r for r in runs.values() if r["status"] == "scheduled" and r["risk"] == "auto"
                                        and r["risk_check"] is None])
            since = datetime.fromisoformat(query.get("finished_since", ["1970-01-01T00:00:00Z"])[0])
            done = sorted((r for r in runs.values() if r["status"] in ("done", "failed", "cancelled")
                           and datetime.fromisoformat(r["finished_at"]) >= since), key=lambda r: r["finished_at"])
            return self._send(200, done[:int(query.get("limit", ["100"])[0])])
        if method == "GET" and (m := re.fullmatch(r"/api/runs/([^/]+)", path)) and m[1] in runs:
            return self._send(200, runs[m[1]])
        if method == "POST" and (m := re.fullmatch(r"/api/runs/([^/]+)/(risk-check|approve)", path)) and m[1] in runs:
            r = runs[m[1]]
            if m[2] == "risk-check":
                if r["status"] != "scheduled":
                    if (r["risk_check"] or {}).get("verdict") == body.get("verdict"):
                        return self._send(200, r)  # the same verdict twice changes nothing
                    return self._send(409, {"error": "conflict", "message": f"run is {r['status']}", "details": []})
                if body.get("verdict") not in ("clear", "hold"):
                    return self._send(422, {"error": "invalid_verdict", "message": "clear or hold", "details": []})
                # while scheduled a hold always applies, even over an earlier clear; a clear only when nothing came yet
                if r["risk_check"] is None or body["verdict"] == "hold":
                    r["risk_check"] = {k: body.get(k) for k in ("verdict", "confidence", "reasons")}
                    if body["verdict"] == "hold":
                        r |= {"status": "waiting_approval", "hold_reasons": body.get("reasons") or []}
                return self._send(200, r)
            if actor in SERVICE_ACTORS or not actor:
                return self._send(403, {"error": "forbidden", "message": f"{actor or 'nobody'} cannot approve a run",
                                        "details": []})
            if r["status"] != "waiting_approval":
                return self._send(409, {"error": "conflict", "message": f"run is {r['status']}", "details": []})
            r |= {"status": "running", "approved_by": actor, "approved_at": now_iso(), "started_at": now_iso()}
            return self._send(200, r)
        if method == "POST" and path == "/api/stub/runs":
            if type(body.get("alert")) is dict:
                alerts[body["alert_id"]] = body["alert"]
            r = {k: v for k, v in body.items() if k not in ("alert", "window_s")} | {
                "id": body.get("id") or f"run-{uuid.uuid4().hex[:8]}", "risk": "auto", "trigger": "auto",
                "status": "scheduled", "risk_check": None,
                "start_at": now_iso(datetime.now(UTC) + timedelta(seconds=body.get("window_s", 30)))}
            runs[r["id"]] = r
            return self._send(201, r)
        if method == "POST" and path == "/api/stub/finished-runs":
            for r in body["runs"]:
                runs[r["id"]] = r
            return self._send(201, {"stored": len(body["runs"])})
        return self._send(404, {"error": "not_found", "message": f"{method} {path}", "details": []})

    def do_GET(self):
        self._route("GET")

    def do_PUT(self):
        self._route("PUT")

    def do_POST(self):
        self._route("POST")


if __name__ == "__main__":
    producer = Producer({"bootstrap.servers": BROKERS})
    print(f"stub api on :{PORT}, Kafka {BROKERS}", file=sys.stderr)
    ThreadingHTTPServer(("0.0.0.0", PORT), Handler).serve_forever()
