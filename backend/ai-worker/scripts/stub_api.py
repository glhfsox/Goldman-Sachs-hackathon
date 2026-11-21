"""A temporary stand-in for the api service (contracts/api.md), just the endpoints the ai-worker calls.

    uv run python scripts/stub_api.py        (port 8000, Kafka from KAFKA_BROKERS, default localhost:9092)

State is in memory. Mappings and runbooks it saves are published to `config.changes` (events.md §6) the way the api
would, so the processor and the ai-worker see them. Remove it once backend/api serves these endpoints.
"""

import json
import os
import re
import sys
import uuid
from datetime import UTC, datetime
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path
from urllib.parse import parse_qs, urlsplit

from confluent_kafka import Producer

BROKERS = os.environ.get("KAFKA_BROKERS", "localhost:9092")
PORT = int(os.environ.get("PORT", "8000"))
FIXTURE = (Path(__file__).parent / "alert_fixture.json").read_text()

mappings, samples, runbooks, proposals = {}, {}, {}, {}
producer = None


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
        if method in ("PUT", "POST") and type(body) is not dict:
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
