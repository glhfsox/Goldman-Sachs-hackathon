"""A tiny client for the api's REST endpoints (contracts/api.md). Base URL is env `API_URL`, `/api` included."""

import json
import os
import urllib.error
import urllib.request
from urllib.parse import quote

TIMEOUT_S = 10


class ApiError(Exception):
    """A 4xx answer: the request itself is wrong, retrying it changes nothing. `body` is `{error, message, details}`."""

    def __init__(self, status: int, body):
        super().__init__(f"api {status}: {body}")
        self.status, self.body = status, body


class Api:
    def __init__(self, base: str | None = None):
        self.base = (base or os.environ["API_URL"]).rstrip("/")

    def get(self, path: str, timeout: float = TIMEOUT_S):
        return self._call("GET", path, None, timeout)

    def put(self, path: str, body):
        return self._call("PUT", path, body)

    def post(self, path: str, body):
        return self._call("POST", path, body)

    def _call(self, method: str, path: str, body, timeout: float = TIMEOUT_S):
        data = None if body is None else json.dumps(body).encode()
        req = urllib.request.Request(self.base + path, data=data, method=method,
                                     headers={"X-User": "ai-worker", "Content-Type": "application/json",
                                              "Accept": "application/json"})
        try:
            with urllib.request.urlopen(req, timeout=timeout) as resp:
                raw = resp.read()
        except urllib.error.HTTPError as e:
            if not 400 <= e.code < 500:
                raise  # 5xx: retryable, like a connection error
            raw = e.read()
            try:
                err = json.loads(raw)
            except ValueError:
                err = raw.decode(errors="replace")
            raise ApiError(e.code, err) from None
        return json.loads(raw) if raw else None


def file_proposal(api: Api, alert_id: str, kind: str, body: dict) -> dict:
    """`POST /alerts/{id}/proposals`. The api learns of an alert from `alerts.changes` and may not have read it yet, so
    its 404 raises a plain error, which retries the job, instead of ApiError, which fails it."""
    try:
        return api.post(f"/alerts/{quote(alert_id, safe='')}/proposals", {"kind": kind, "body": body})
    except ApiError as e:
        if e.status != 404:
            raise
        raise RuntimeError(f"the api does not know alert {alert_id} yet: {e}") from None
