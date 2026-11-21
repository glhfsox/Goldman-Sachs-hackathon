"""The run judges: verdict rule, late jobs, 409, the finished-runs copy and its trigger, stats, polling a bad api."""

import time
from datetime import UTC, datetime, timedelta
from types import SimpleNamespace

import pytest

from ai_worker import jobs, main, runs
from ai_worker.api import ApiError

NOW = datetime(2026, 11, 21, 10, 0, tzinfo=UTC)
RUN = {"id": "run-1", "alert_id": "A1", "rule_id": "r1", "team_id": "settlements", "runbook_id": "rb1",
       "risk": "auto", "trigger": "auto", "status": "scheduled", "start_at": "2026-11-21T10:00:30Z",
       "risk_check": None, "nodes": [{"id": "retry", "action": "retry_workflow", "risk": "auto",
                                      "params": {"workflowId": "WF-1"}, "text": "Retry the workflow"}]}


def iso(t):
    return t.isoformat().replace("+00:00", "Z")


def answer(name, choice, confidence, factors=0.9, options=("clear", "hold")):
    other = [o for o in options if o != choice][0]
    return {"answers": {name: {"type": "choice", "choice": choice, "confidence": confidence,
                               "probabilities": {choice: confidence, other: round(1 - confidence, 3)}}}
            | {k: {"type": "noul", "noul": factors} for k in runs.FACTORS}}


def fake_ctx(resp=None, post_error=None, run_stats=None):
    calls = []

    def post(path, body):
        calls.append(("POST", path, body))
        if post_error:
            raise post_error
        return {"id": "prop-1"}

    api = SimpleNamespace(get=lambda path, *_: calls.append(("GET", path)) or {"alert": {
        "title": "WF failed", "severity": "critical", "acked_by": "marek",
        "sample_event": {"type": "WORKFLOW_FAILED", "service": "order-settlement", "env": "prod", "raw": {"x": 1}}}},
        post=post)
    return SimpleNamespace(api=api, rules={"r1": {"name": "Settlement failed", "when": {"all": []}}}, now=lambda: NOW,
                           ask_jev=lambda state, q: calls.append(("jev", state, q)) or resp,
                           run_stats=run_stats), calls


@pytest.mark.parametrize("resp, verdict", [
    (answer("decision", "clear", 0.8, factors=0.1), "clear"),
    (answer("decision", "clear", 0.55), "hold"),
    (answer("decision", "hold", 0.9), "hold"),
])
def test_verdict_is_clear_only_when_jev_is_sure(resp, verdict):
    ctx, calls = fake_ctx(resp)
    result = runs.risk(RUN, ctx)
    (_, get_path), (_, state, questions), (_, path, body) = calls
    assert get_path == "/alerts/A1" and path == "/runs/run-1/risk-check" and body["verdict"] == verdict
    assert state["alert"] == {"title": "WF failed", "severity": "critical", "type": "WORKFLOW_FAILED",
                              "service": "order-settlement", "env": "prod"}  # no raw payload, no user ids
    assert state["run"]["nodes"][0]["params"] == {"workflowId": "WF-1"} and set(questions) == {"decision", *runs.FACTORS}
    assert body["confidence"] == resp["answers"]["decision"]["probabilities"][verdict] and result["posted"]
    assert (body["reasons"] == []) if verdict == "clear" else ("it runs in production" in body["reasons"]
                                                                or "not sure enough" in body["reasons"][-1])


def test_clear_needs_the_probability_too():
    resp = answer("decision", "clear", 0.9, factors=0.1)
    resp["answers"]["decision"]["probabilities"] = {"clear": 0.05, "hold": 0.95}  # inconsistent answer
    ctx, calls = fake_ctx(resp)
    assert runs.risk(RUN, ctx)["verdict"] == "hold" and calls[-1][2]["verdict"] == "hold"


def test_a_run_too_large_to_check_is_held_without_jev():
    big = RUN | {"nodes": [{"action": "notify", "risk": "auto", "text": "x" * 1900} for _ in range(10)]}
    ctx, calls = fake_ctx()
    result = runs.risk(big, ctx)
    assert result["verdict"] == "hold" and result["reasons"] == ["the run is too large to check"]
    assert [c[0] for c in calls] == ["GET", "POST"]  # no Jev call


@pytest.mark.parametrize("junk", [{"id": "run-2"}, RUN | {"nodes": None}, RUN | {"start_at": "soon"},
                                  RUN | {"rule_id": 7}, "run-3"])
def test_pending_jobs_drop_junk(junk):
    api = SimpleNamespace(get=lambda *_: [junk, RUN])
    assert runs.pending_jobs(api) == [("run_risk:run-1", "run_risk", RUN)]


def test_run_risk_is_claimed_before_an_older_job(conns):
    pg, jobs_pg = conns
    jobs.enqueue(pg, "schema:fp1", "mapping", {})
    jobs.enqueue(pg, "run_risk:run-1", "run_risk", RUN)
    assert jobs.claim(jobs_pg, ["mapping", "run_risk"])["id"] == "run_risk:run-1"
    jobs_pg.rollback()


def test_malformed_answer_is_retried_and_posts_nothing():
    ctx, calls = fake_ctx({"answers": {"decision": {"choice": "maybe"}}})
    with pytest.raises(ValueError):
        runs.risk(RUN, ctx)
    assert [c[0] for c in calls] == ["GET", "jev"]  # nothing posted


def test_late_job_does_not_call_jev():
    ctx, calls = fake_ctx()
    assert runs.risk(RUN | {"start_at": iso(NOW - timedelta(seconds=1))}, ctx)["verdict"] is None and calls == []


def test_409_means_too_late_not_an_error():
    ctx, _ = fake_ctx(answer("decision", "hold", 0.9), post_error=ApiError(409, {"error": "conflict"}))
    assert runs.risk(RUN, ctx)["posted"] is False


def finished(i, rule="r1", runbook="rb1", **kw):
    t = NOW - timedelta(hours=1) + timedelta(minutes=i)
    return {"id": f"run-{rule}-{runbook}-{i}", "alert_id": f"A{i}", "rule_id": rule, "runbook_id": runbook,
            "team_id": "settlements", "risk": "approval", "trigger": "manual", "status": "done",
            "start_at": iso(t), "approved_by": "marek", "approved_at": iso(t + timedelta(seconds=8)),
            "started_at": iso(t + timedelta(seconds=8)), "finished_at": iso(t + timedelta(seconds=20)),
            "cancelled_by": None, "risk_check": None,
            "nodes": [{"id": "retry", "action": "retry_workflow", "risk": "auto", "params": {}, "status": "succeeded"}]} | kw


def api_returning(*answers):
    seen, answers = [], list(answers)
    return SimpleNamespace(get=lambda path, *_: seen.append(path) or answers.pop(0)), seen


def test_finished_runs_bucket_trigger_with_a_skipped_multiple(conns):
    pg, _ = conns
    api, seen = api_returning([finished(i) for i in range(9)] + [{"id": "junk"}, "junk"],
                              [finished(i) for i in range(9, 12)], [finished(11)])
    found, cursor = runs.sync_finished(pg, api, None, NOW)
    assert found == [] and seen[0].startswith("/runs?finished_since=1970-01-01T00:00:00Z")
    job, cursor = runs.sync_finished(pg, api, cursor, NOW)  # 9 -> 12: the exact 10th was never seen alone
    assert job == [("run_start:r1:rb1:10", "run_start", {"rule_id": "r1", "runbook_id": "rb1"})]
    assert "finished_since=2026-11-21T09%3A03%3A20%2B00%3A00" in seen[1]  # newest seen minus OVERLAP
    assert jobs.enqueue(pg, *job[0]) and not jobs.enqueue(pg, *runs.sync_finished(pg, api, None, NOW)[0][0])


def test_a_future_dated_run_is_skipped_and_does_not_move_the_cursor(conns):
    pg, _ = conns
    api, _ = api_returning([finished(1), finished(2, finished_at=iso(NOW + timedelta(days=1)))])
    found, cursor = runs.sync_finished(pg, api, None, NOW)
    assert cursor == NOW - timedelta(hours=1) + timedelta(minutes=1, seconds=20)
    assert pg.execute("SELECT array_agg(run_id) FROM run_history").fetchone()[0] == ["run-r1-rb1-1"]


def test_run_start_job_id_quotes_both_ids(conns):
    pg, _ = conns
    api, _ = api_returning([finished(i, rule="a:b", runbook="c/d") for i in range(10)])
    assert runs.sync_finished(pg, api, None, NOW)[0][0][0] == "run_start:a%3Ab:c%2Fd:10"


def test_stats_on_a_small_fixture(conns):
    pg, _ = conns
    api, _ = api_returning([finished(i) for i in range(8)]
                           + [finished(8, status="cancelled", approved_by=None, started_at=None, cancelled_by="anna"),
                              finished(9, status="failed", trigger="auto", risk_check={"verdict": "hold"}),
                              finished(10, rule="other")]
                           + [finished(11, finished_at=iso(NOW - timedelta(days=40)))])
    runs.sync_finished(pg, api, None, NOW)
    s, latest = runs.stats(pg, "r1", "rb1", NOW)
    expected = {"window": {"max_runs": 50, "max_age_days": 30}, "runs": 10, "auto_started_share": 0.1,
                "approved_share": 0.9, "median_seconds_from_alert_to_approve": None,
                "cancelled_before_start_share": 0.1, "cancelled_after_start_share": 0.0, "held_share": 0.1,
                "failed_share": 0.1, "rule_alerts": None, "alerts_without_run": None, "alerts_without_run_share": None}
    assert s == expected  # no alert history: the alert fields are null
    t0 = NOW - timedelta(hours=1)
    for i in range(10):  # each alert opened 2 s before its run's start_at, so approval is 10 s after the alert
        pg.execute("INSERT INTO alert_history (alert_id, rule_id, opened_at) VALUES (%s, 'r1', %s)",
                   (f"A{i}", t0 + timedelta(minutes=i, seconds=-2)))
    pg.execute("INSERT INTO alert_history (alert_id, rule_id, opened_at) VALUES ('A20', 'r1', %s), ('A21', 'r1', %s)",
               (t0, NOW - timedelta(days=40)))  # A20 got no run; A21 is outside the window
    s, latest = runs.stats(pg, "r1", "rb1", NOW)
    assert s == expected | {"median_seconds_from_alert_to_approve": 10, "rule_alerts": 11, "alerts_without_run": 1,
                            "alerts_without_run_share": 0.091}
    assert runs._stat_reasons(s)[-1] == "1 of 11 alerts of this rule got no run"
    assert latest == {"alert_id": "A9", "team_id": "settlements", "trigger": "auto",
                      "actions": [{"action": "retry_workflow", "risk": "auto"}]}


START_STATS = {"window": {"max_runs": 50, "max_age_days": 30}, "runs": 20, "auto_started_share": 0.0,
               "approved_share": 1.0, "median_seconds_from_alert_to_approve": 9, "cancelled_before_start_share": 0.0,
               "cancelled_after_start_share": 0.0, "held_share": 0.0, "failed_share": 0.0,
               "rule_alerts": None, "alerts_without_run": None, "alerts_without_run_share": None}
LATEST = {"alert_id": "A9", "team_id": "settlements", "trigger": "manual", "actions": []}


@pytest.mark.parametrize("resp, proposed", [(answer("start", "auto", 0.78, 0.1, ("auto", "approval")), "auto"),
                                            (answer("start", "approval", 0.9, 0.1, ("auto", "approval")), None),
                                            (answer("start", "auto", 0.5, 0.1, ("auto", "approval")), None)])
def test_start_proposal_only_when_confidently_different(resp, proposed):
    ctx, calls = fake_ctx(resp, run_stats=lambda r, b: (START_STATS, LATEST))
    result = runs.start({"rule_id": "r1", "runbook_id": "rb1"}, ctx)
    assert result["proposed"] == proposed
    if proposed:
        (_, path, body), = [c for c in calls if c[0] == "POST"]
        assert path == "/alerts/A9/proposals" and body["kind"] == "runbook_start"
        assert body["body"] == {"rule_id": "r1", "team_id": "settlements", "runbook_id": "rb1", "start": "auto",
                                "current_start": "approval", "confidence": 0.78,
                                "probabilities": {"auto": 0.78, "approval": 0.22}, "stats": START_STATS,
                                "reasons": ["20 of 20 runs were approved by a person, median 9 s after the alert",
                                            "no run was cancelled", "no run failed"]}
    ctx, calls = fake_ctx(resp, run_stats=lambda r, b: (START_STATS | {"runs": 7}, LATEST))
    assert "not enough recent runs: 7" in runs.start({"rule_id": "r1", "runbook_id": "rb1"}, ctx)["why"]
    if proposed:  # half the window started by itself: today is already `auto`
        ctx, _ = fake_ctx(resp, run_stats=lambda r, b: (START_STATS | {"auto_started_share": 0.5}, LATEST))
        assert runs.start({"rule_id": "r1", "runbook_id": "rb1"}, ctx)["why"] == "same as today"
    ctx, calls = fake_ctx(resp, run_stats=lambda r, b: (START_STATS, LATEST))
    ctx.rules["r1"]["enabled"] = False
    assert "disabled" in runs.start({"rule_id": "r1", "runbook_id": "rb1"}, ctx)["why"] and calls == []


@pytest.mark.parametrize("err", [ApiError(404, {"error": "not_found"}), ConnectionRefusedError("down"),
                                 ValueError("junk")])
def test_an_api_without_runs_does_not_break_polling(conns, err, caplog):
    pg, jobs_pg = conns

    def get(path, *_):
        raise err
    w = main.Worker(pg, jobs_pg, None, None, None, SimpleNamespace(get=get))
    w.poll_runs()
    wait = w.runs_due["pending"] - time.monotonic()
    assert 25 < wait <= 30 if isinstance(err, ApiError) else 0 < wait <= 5  # pending: 5 s, a 404: 30 s
    w.runs_due = dict.fromkeys(w.runs_due, 0.0)
    w.poll_runs()  # still failing: no second warning
    assert w.runs_down == {"pending", "finished"}
    assert len([r for r in caplog.records if "polling" in r.getMessage()]) == 2
