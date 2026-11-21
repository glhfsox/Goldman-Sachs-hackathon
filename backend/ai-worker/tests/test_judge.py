"""The severity judge: history rows, triggers, stats, the Jev call and what gets filed (fake Jev, fake api)."""

import json
import threading
from datetime import UTC, datetime, timedelta
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from types import SimpleNamespace

import pytest

from ai_worker import jobs, judge
from ai_worker.agent import JobFailed
from ai_worker.api import ApiError, file_proposal

T0 = datetime(2026, 11, 1, 10, 0, tzinfo=UTC)
RULE = {"id": "r1", "team_id": "settlements", "name": "Settlement failed", "enabled": True,
        "when": {"all": [{"field": "type", "op": "eq", "value": "WORKFLOW_FAILED"}]},
        "route": {"recipients": [{"team": "settlements"}]}, "present": {"title": "{{workflowName}} failed"},
        "severity": "warning"}


def iso(t: datetime) -> str:
    return t.isoformat(timespec="milliseconds").replace("+00:00", "Z")


def change(change, alert_id, at, actor="processor", full=True, **alert):
    a = {"id": alert_id}
    if full:
        a |= {"rule_id": "r1", "team_id": "settlements", "severity": "critical", "count": 3, "title": "WF failed",
              "body": "timeout", "fields": {"step": "settle"}, "first_received_at": iso(T0),
              "sample_event": {"type": "WORKFLOW_FAILED", "service": "order-settlement", "env": "prod",
                               "severity": "critical", "raw": {"big": "x"}}} | alert
    return {"change_id": f"{alert_id}:{change}", "change": change, "at": iso(at), "actor": actor, "alert": a}


def row(pg, alert_id):
    return pg.execute("SELECT rule_id, count, opened_at, acked_at, acked_by, resolved_at, resolved_by, reopened"
                      " FROM alert_history WHERE alert_id = %s", (alert_id,)).fetchone()


def test_history_upsert_is_idempotent_and_takes_id_only_records(conns):
    pg, _ = conns
    t1, t2 = T0 + timedelta(minutes=3), T0 + timedelta(minutes=9)
    for rec in [change("opened", "A", T0), change("opened", "A", T0, count=1),
                change("acked", "A", t1, "marek", full=False), change("acked", "A", t2, "anna", full=False),
                change("reopened", "A", t1), change("resolved", "A", t2, "auto", full=False),
                change("resolved", "A", t1, "marek", full=False)]:  # a late redelivery of an older resolve
        judge.record(pg, rec)
    assert row(pg, "A") == ("r1", 3, T0, t1, "marek", t2, "auto", True)
    assert judge.record(pg, change("acked", "B", t1, "marek", full=False)) is None  # unknown id-only: skipped
    assert row(pg, "B") is None


def test_triggers_enqueue_once_at_the_right_moments(conns):
    pg, _ = conns
    first = judge.record(pg, change("opened", "A0", T0))
    assert first[:2] == ("severity:r1:first", "severity")
    assert first[2] == {"rule_id": "r1", "alert_id": "A0", "mode": "first",
                        "example": {"title": "WF failed", "body": "timeout", "fields": {"step": "settle"},
                                    "type": "WORKFLOW_FAILED", "service": "order-settlement", "env": "prod",
                                    "severity": "critical"}}
    assert jobs.enqueue(pg, *first) and not jobs.enqueue(pg, *judge.record(pg, change("opened", "A0", T0)))
    got = []
    for i in range(20):
        judge.record(pg, change("opened", f"A{i}", T0))
        got.append(judge.record(pg, change("resolved", f"A{i}", T0 + timedelta(minutes=i), "marek", full=False)))
    assert got[:19] == [None] * 19
    assert got[19][0] == "severity:r1:20" and got[19][2]["mode"] == "history" and got[19][2]["example"] is None
    assert jobs.enqueue(pg, *got[19])
    again = judge.record(pg, change("resolved", "A19", T0, "marek", full=False))  # redelivered
    assert again[0] == "severity:r1:20" and not jobs.enqueue(pg, *again)
    judge.record(pg, change("reopened", "A3", T0))  # a reopened alert resolved again adds no new job id
    assert judge.record(pg, change("resolved", "A3", T0 + timedelta(hours=1), "marek", full=False))[0] == "severity:r1:20"
    assert judge.record(pg, change("opened", "A20", T0)) is None  # the rule has resolved alerts now


def test_a_skipped_multiple_is_enqueued_by_the_next_resolve(conns):
    pg, _ = conns  # e.g. two replicas each resolved one alert at 19, or a crash between upsert and enqueue
    history(pg, 19)
    for aid in ("X", "Y"):
        judge.record(pg, change("opened", aid, T0))
        pg.execute("UPDATE alert_history SET resolved_at = %s, resolved_by = 'marek' WHERE alert_id = %s", (T0, aid))
    job = judge.record(pg, change("resolved", "Z", T0, "marek"))  # count goes 21 -> 22
    assert job[0] == "severity:r1:20" and jobs.enqueue(pg, *job)


def history(pg, n, start=T0, rule="r1"):
    for i in range(n):
        acked = i % 4 != 0  # 3 of 4 acked, 3 min after opening; the rest resolve on their own
        a = {"rule_id": rule, "count": i + 1, "first_received_at": iso(start)}
        judge.record(pg, change("opened", f"{rule}-{start:%j}-{i}", start, **a))
        if acked:
            judge.record(pg, change("acked", f"{rule}-{start:%j}-{i}", start + timedelta(minutes=3), "marek", **a))
        judge.record(pg, change("resolved", f"{rule}-{start:%j}-{i}", start + timedelta(minutes=10),
                                "marek" if acked else "auto", full=False))


def test_stats_and_the_age_window(conns):
    pg, _ = conns
    history(pg, 20, start=T0 - timedelta(days=40))  # too old
    history(pg, 20)
    history(pg, 5, rule="other")
    s = judge.stats(pg, "r1", now=T0 + timedelta(days=1))
    assert s == {"window": {"max_alerts": 200, "max_age_days": 30}, "resolved_alerts": 20, "acked_share": 0.75,
                 "median_minutes_to_ack": 3.0, "auto_resolved_unacked_share": 0.25, "reopened_share": 0.0,
                 "median_count": 10.5, "severities": {"critical": 20}}
    assert judge.stats(pg, "r1", now=T0 + timedelta(days=31))["resolved_alerts"] == 0


def answer(choice="critical", confidence=0.8, factors=0.9):
    probs = dict.fromkeys(judge.SEVERITIES, round((1 - confidence) / 3, 4)) | {choice: confidence}
    return {"model": "jev-1.14.0", "usage": {"input_tokens": 1, "output_tokens": 1},
            "answers": {"severity": {"type": "choice", "choice": choice, "confidence": confidence,
                                     "probabilities": probs}}
            | {k: {"type": "noul", "noul": factors} for k in judge.FACTORS | judge.HISTORY_FACTORS}}


def fake_ctx(resp, stats=None, alert=None):
    calls = []

    def ask_jev(state, questions):
        calls.append(("jev", state, questions))
        return resp

    api = SimpleNamespace(get=lambda path: calls.append(("GET", path)) or {"alert": alert},
                          post=lambda path, body, headers=None: calls.append(("POST", path, body)) or {"id": "prop-1"})
    return SimpleNamespace(api=api, job_id="job-1", rules={"r1": RULE}, ask_jev=ask_jev, rule_stats=lambda rid: stats), calls


STATS = {"window": {"max_alerts": 200, "max_age_days": 30}, "resolved_alerts": 20, "acked_share": 0.7,
         "median_minutes_to_ack": 3.0, "auto_resolved_unacked_share": 0.25, "reopened_share": 0.05,
         "median_count": 4.0, "severities": {"critical": 20}}


def test_confident_different_answer_files_one_proposal():
    ctx, calls = fake_ctx(answer(), stats=STATS,
                          alert={"title": "WF failed", "sample_event": {"type": "WORKFLOW_FAILED", "env": "prod"}})
    result = judge.handle({"rule_id": "r1", "alert_id": "A19", "mode": "history", "example": None}, ctx)
    (_, get_path), (_, state, questions), (_, path, body) = calls
    assert get_path == "/alerts/A19" and state["example_alert"]["title"] == "WF failed"
    assert state["rule"]["severity"] == "warning" and state["history"] == STATS
    assert set(questions) == {"severity", *judge.FACTORS, *judge.HISTORY_FACTORS}
    assert questions["severity"]["type"] == "choice" and set(questions["severity"]["criteria"]) == set(judge.SEVERITIES)
    assert path == "/alerts/A19/proposals" and result["proposal_id"] == "prop-1"
    assert body["kind"] == "rule_severity"
    b = body["body"]
    assert {k: b[k] for k in ("rule_id", "team_id", "severity", "current_severity", "confidence", "mode", "stats")} == \
        {"rule_id": "r1", "team_id": "settlements", "severity": "critical", "current_severity": "warning",
         "confidence": 0.8, "mode": "history", "stats": STATS}
    assert set(b["probabilities"]) == set(judge.SEVERITIES)
    assert "it comes from production" in b["reasons"]
    assert "14 of 20 alerts were acknowledged by a person, median 3 min" in b["reasons"]
    assert "1 of 20 were reopened" in b["reasons"]


def test_first_mode_uses_the_record_and_asks_no_history_factors():
    ctx, calls = fake_ctx(answer(factors=0.2))
    example = {"title": "WF failed", "body": "x" * 5000}
    judge.handle({"rule_id": "r1", "alert_id": "A0", "mode": "first", "example": example}, ctx)
    (_, state, questions), (_, _, body) = calls
    assert set(questions) == {"severity", *judge.FACTORS} and state["history"] is None
    assert len(state["example_alert"]["body"]) < 2100 and "[truncated" in state["example_alert"]["body"]
    assert body["body"]["mode"] == "first" and body["body"]["stats"] is None and body["body"]["reasons"] == []


@pytest.mark.parametrize("resp, why", [(answer("warning"), "same as the rule's severity"),
                                       (answer(confidence=0.4), "confidence below 0.5")])
def test_same_severity_or_low_confidence_files_nothing(resp, why):
    ctx, calls = fake_ctx(resp)
    result = judge.handle({"rule_id": "r1", "alert_id": "A0", "mode": "first", "example": {"title": "t"}}, ctx)
    assert result["proposed"] is None and result["why"] == why and result["answer"]["choice"] == resp[
        "answers"]["severity"]["choice"]
    assert [c[0] for c in calls] == ["jev"]


def test_little_recent_history_or_an_unknown_rule_files_nothing():
    ctx, calls = fake_ctx(answer(), stats=STATS | {"resolved_alerts": 12})
    result = judge.handle({"rule_id": "r1", "alert_id": "A", "mode": "history", "example": {"title": "t"}}, ctx)
    assert result["proposed"] is None and "not enough recent history: 12" in result["why"] and calls == []
    result = judge.handle({"rule_id": "gone", "alert_id": "A", "mode": "first", "example": None}, ctx)
    assert result["proposed"] is None and calls == []


def test_little_recent_history_end_to_end_with_the_table(conns):
    pg, _ = conns
    history(pg, 20, start=T0 - timedelta(days=40))
    history(pg, 19)
    ctx, calls = fake_ctx(answer())
    ctx.rule_stats = lambda rid: judge.stats(pg, rid, now=T0)
    result = judge.handle({"rule_id": "r1", "alert_id": "A", "mode": "history", "example": {"title": "t"}}, ctx)
    assert "not enough recent history: 19" in result["why"] and calls == []


@pytest.mark.parametrize("resp", [
    {"answers": {}},
    answer() | {"answers": answer()["answers"] | {"severity": {"choice": "fatal", "confidence": 0.9,
                                                               "probabilities": {}}}},
    answer() | {"answers": answer()["answers"] | {"production": {"noul": "yes"}}},
    [],
])
def test_malformed_jev_response_is_retryable(resp):
    ctx, _ = fake_ctx(resp)
    with pytest.raises(ValueError):
        judge.handle({"rule_id": "r1", "alert_id": "A0", "mode": "first", "example": {"title": "t"}}, ctx)


@pytest.fixture
def jev(monkeypatch):
    """A local stand-in for TypeSafe: answers each request with the next (status, body) and records requests."""
    replies, seen = [], []

    class H(BaseHTTPRequestHandler):
        def do_POST(self):
            seen.append((self.path, self.headers["Authorization"], json.loads(self.rfile.read(
                int(self.headers["Content-Length"])))))
            status, body = replies.pop(0)
            data = json.dumps(body).encode()
            self.send_response(status)
            self.send_header("Content-Length", str(len(data)))
            self.end_headers()
            self.wfile.write(data)

        def log_message(self, *_):
            pass

    srv = ThreadingHTTPServer(("127.0.0.1", 0), H)
    threading.Thread(target=srv.serve_forever, daemon=True).start()
    monkeypatch.setenv("TYPESAFE_BASE_URL", f"http://127.0.0.1:{srv.server_port}/")
    monkeypatch.setenv("TYPESAFE_API_KEY", "test-key")
    monkeypatch.delenv("TYPESAFE_MODEL", raising=False)
    yield replies, seen
    srv.shutdown()


def test_ask_jev_posts_the_request_and_sorts_errors(jev):
    replies, seen = jev
    replies += [(200, answer()), (422, {"detail": "unknown question type"}), (429, {"detail": "slow down"}),
                (529, {"detail": "overloaded"})]
    assert judge.ask_jev({"a": 1}, {"q": {"type": "noul", "instructions": "x"}}) == answer()
    assert seen[0] == ("/v1/systemone", "Bearer test-key",
                       {"model": "jev-latest", "state": {"a": 1}, "questions": {"q": {"type": "noul", "instructions": "x"}}})
    with pytest.raises(JobFailed, match="jev 422: unknown question type"):
        judge.ask_jev({}, {})
    for _ in range(2):
        with pytest.raises(RuntimeError) as e:
            judge.ask_jev({}, {})
        assert not isinstance(e.value, JobFailed) and "test-key" not in str(e.value)


@pytest.mark.parametrize("status, raised", [(404, RuntimeError), (422, ApiError)])
def test_file_proposal_retries_an_alert_the_api_has_not_read_yet(status, raised):
    def post(path, body, headers=None):
        raise ApiError(status, {"error": "x"})

    with pytest.raises(raised):  # RuntimeError retries the job (main.run_one), ApiError fails it
        file_proposal(SimpleNamespace(post=post), "A1", "rule_severity", {}, "job-1")
