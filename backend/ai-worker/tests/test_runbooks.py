"""The runbook tools with a fake ctx: run_agent feeds scripted submissions to the tool, the api records calls."""

import copy
import json
from pathlib import Path
from types import SimpleNamespace

import pytest

from ai_worker import runbooks
from ai_worker.agent import JobFailed
from ai_worker.api import ApiError
from anthropic.lib.tools import ToolError

FIXTURE = json.loads((Path(__file__).parent.parent / "scripts" / "alert_fixture.json").read_text())
MARKDOWN = """# HighErrorRate on payments-api
1. Open the payments dashboard and check if errors are in one region only.
2. If one region: fail traffic over to the other region (needs approval from the team lead).
3. If all regions: check the last deployment and roll it back.
4. Post an update in the #payments-incidents channel every 15 minutes."""
YAML = """runbook: settlement-timeout
matches: {type: WORKFLOW_FAILED, "error.code": TIMEOUT}
steps:
  - check: "Is counterparty-service healthy?"
    action: http_get
    url: "http://counterparty-service/health"
  - action: retry_workflow
    workflowId: "{{workflowId}}"
    approval: none
  - if: "retry fails twice"
    action: escalate
    team: counterparty-support
    approval: required"""


ACTIONS = [{"name": n, "risk": r, "default_target": t} for n, r, t in [
    ("http_get", "read_only", None), ("retry_workflow", "auto", "workflow-engine"), ("reload_db", "auto", "orders-db"),
    ("restart_service", "approval", "services"), ("webhook", "approval", None), ("ticket", "approval", "ticketing"),
    ("escalate", "approval", "escalation")]]
TARGETS = [{"id": "pricing-engine", "origins": ["http://pricing-engine"], "operations": {
    "http_get": {"method": "GET", "path": "/health"},
    "restart_service": {"method": "POST", "path": "/instances/{instance}/restart"}}}]


def fake_ctx(submissions, alert_doc=None, refuse=0, runbook=None):
    """`refuse`: how many saves the api answers with 422 before it accepts one."""
    calls, replies = [], []

    def run_agent(system, user, tool):
        calls.append(("agent", system, user, tool.to_dict()))
        for s in submissions:
            try:
                replies.append(tool.call(copy.deepcopy(s)))
            except ToolError as e:  # as agent._run_tool does: the error text goes back to the model
                replies.append(str(e))

    def save(method, path, body, headers=None):
        calls.append((method, path, body))
        if sum(c[0] in ("PUT", "POST") for c in calls) <= refuse:
            raise ApiError(422, {"error": "invalid_runbook", "details": [
                {"path": "steps[0].params", "message": "the step does not resolve to an action target"}]})
        return {"id": "prop-1"}

    def get(path):
        return {"/actions": ACTIONS, "/action-targets": TARGETS}.get(path) or (
            (runbook or {"status": "draft", "canonical": None, "version": 1}) if path.startswith("/runbooks/")
            else alert_doc)

    api = SimpleNamespace(get=get, put=lambda path, body: save("PUT", path, body),
                          post=lambda path, body, headers=None: save("POST", path, body, headers))
    return SimpleNamespace(api=api, run_agent=run_agent, job_id="job-1"), calls, replies


def record(fmt, source):
    return {"id": "rb-1", "team_id": "payments", "title": "HighErrorRate", "format": fmt, "source": source,
            "canonical": None, "status": "draft", "version": 1}


@pytest.mark.parametrize("step, problem", [
    ({"action": "http_get"}, "text: required"),
    ({"text": "x", "colour": "red"}, "unknown key 'colour'"),
    ({"text": "x", "action": "failover_region"}, "'failover_region' is not a registered action"),
])
def test_validator_rejects_bad_steps(step, problem):
    assert any(problem in p for p in runbooks.check_runbook({"id": "a", "title": "t", "steps": [step]}, {"http_get": True}))


def test_validator_rejects_bad_matches_and_accepts_the_contract_example():
    bad = {"id": "a", "title": "t", "matches": {"all": [{"field": "type", "op": "gtt", "value": 1}]},
           "steps": [{"text": "x"}]}
    assert any(p.startswith("matches.") for p in runbooks.check_runbook(bad, {}))
    ok = {**bad, "matches": {"all": [{"field": "type", "op": "eq", "value": "WORKFLOW_FAILED"}]}}
    assert runbooks.check_runbook(ok, {}) == []


@pytest.mark.parametrize("step, approval", [
    ({"text": "Restart", "action": "restart_service", "approval": "none"}, "required"),  # no quote
    ({"text": "Restart", "action": "restart_service", "approval": "none", "approval_quote": "no  APPROVAL needed"},
     "none"),
    ({"text": "Restart", "action": "restart_service", "approval": "none", "approval_quote": "approval: none"},
     "required"),  # not in the source
    ({"text": "Restart", "action": "restart_service", "approval": "none", "approval_quote": "Restart the instance"},
     "required"),  # in the source, but says nothing about approval
    ({"text": "Check", "action": "http_get", "params": {"url": "http://x/health"}, "approval": "required"}, "none"),
])
def test_approval_is_decided_by_code(step, approval):
    ctx, calls, _ = fake_ctx([{"steps": [step]}])
    runbooks.convert(record("text", "Restart the instance, no approval\nneeded."), ctx)
    assert calls[-1][2]["canonical"]["steps"][0]["approval"] == approval
    assert "approval_quote" not in calls[-1][2]["canonical"]["steps"][0]


def test_link_runbook_needs_no_model():
    ctx, calls, _ = fake_ctx([])
    assert runbooks.convert(record("link", " https://wiki.example.com/rb \n"), ctx) == {"runbook_id": "rb-1", "steps": 1}
    assert calls == [("PUT", "/runbooks/rb-1", {"status": "active", "canonical": {
        "id": "rb-1", "title": "HighErrorRate", "steps": [{"text": "Open the runbook", "link": "https://wiki.example.com/rb"}]}})]


def test_convert_markdown_fixes_an_unregistered_action_then_saves():
    steps = [{"text": "Open the payments dashboard and check if errors are in one region only."},
             {"text": "Fail traffic over to the other region (needs approval from the team lead).", "when": "one region"},
             {"text": "Check the last deployment and roll it back.", "when": "all regions"},
             {"text": "Post an update in the #payments-incidents channel every 15 minutes."}]
    bad = copy.deepcopy(steps)
    bad[1]["action"] = "failover_region"
    ctx, calls, replies = fake_ctx([{"steps": bad, "matches": {"all": [
        {"field": "service", "op": "eq", "value": "payments-api"}]}}, {"steps": steps}])
    assert runbooks.convert(record("markdown", MARKDOWN), ctx) == {"runbook_id": "rb-1", "steps": 4}
    assert "failover_region" in replies[0] and "Not accepted" in replies[0] and replies[1].startswith("Accepted")
    agent, put = calls
    assert "submit_runbook" in agent[1] and "untrusted" in agent[1] and MARKDOWN in agent[2]
    assert agent[3]["name"] == "submit_runbook"
    assert put == ("PUT", "/runbooks/rb-1", {"status": "active",
                                             "canonical": {"id": "rb-1", "title": "HighErrorRate", "steps": steps}})


def test_convert_feeds_the_apis_refusal_back_to_the_model():
    step = {"text": "Restart the pricing-engine instance", "action": "restart_service",
            "params": {"target": "pricing-engine", "instance": "{{instance}}"}}
    ctx, calls, replies = fake_ctx([{"steps": [{**step, "params": {"host": "x"}}]}, {"steps": [step]}], refuse=1)
    assert runbooks.convert(record("text", "Restart the pricing-engine instance named in the alert"), ctx)["steps"] == 1
    assert "steps[0].params: the step does not resolve" in replies[0] and replies[1].startswith("Accepted")
    system = calls[0][1]
    assert "pricing-engine (origins http://pricing-engine)" in system and "reload_db" in system
    assert calls[-1][2]["canonical"]["steps"][0]["params"]["target"] == "pricing-engine"


def test_an_api_outage_while_saving_stops_the_model_and_retries_the_job():
    ctx, calls, replies = fake_ctx([{"steps": [{"text": "x"}]}, {"steps": [{"text": "x"}]}])
    ctx.api.put = lambda path, body: (_ for _ in ()).throw(ConnectionRefusedError("api down"))
    with pytest.raises(ConnectionRefusedError):  # not JobFailed: main.run_one retries it with backoff
        runbooks.convert(record("text", "do things"), ctx)
    assert replies == [runbooks.STOP, runbooks.STOP]


def test_convert_skips_a_runbook_already_converted():
    ctx, calls, _ = fake_ctx([], runbook={"status": "active", "canonical": {"id": "rb-1"}, "version": 2})
    assert "skipped" in runbooks.convert(record("text", "do things"), ctx) and calls == []


def test_convert_yaml_keeps_a_quoted_approval_none():
    ctx, calls, _ = fake_ctx([{
        "matches": {"all": [{"field": "type", "op": "eq", "value": "WORKFLOW_FAILED"},
                            {"field": "error.code", "op": "eq", "value": "TIMEOUT"}]},
        "steps": [{"text": "Is counterparty-service healthy?", "action": "http_get",
                   "params": {"url": "http://counterparty-service/health"}},
                  {"text": "Retry the workflow", "action": "retry_workflow", "params": {"workflowId": "{{workflowId}}"},
                   "approval": "none", "approval_quote": "approval: none"},
                  {"text": "Escalate to counterparty-support", "when": "retry fails twice", "action": "escalate",
                   "params": {"team": "counterparty-support"}, "approval": "none"}]}])
    runbooks.convert(record("yaml", YAML), ctx)
    canonical = calls[-1][2]["canonical"]
    assert [s["approval"] for s in canonical["steps"]] == ["none", "none", "required"]
    assert canonical["matches"]["all"][1]["field"] == "error.code"


def test_convert_fails_when_nothing_valid_was_submitted():
    ctx, calls, _ = fake_ctx([{"steps": []}])
    with pytest.raises(JobFailed):
        runbooks.convert(record("text", "do things"), ctx)
    assert [c[0] for c in calls] == ["agent"]


def alert_doc():
    doc = json.loads(json.dumps(FIXTURE).replace("ALERT_ID", "a-1"))
    return {"alert": doc["alert"], "incident": None, "timeline": doc["timeline"], "notifications": [], "runbook": None}


def test_propose_skips_without_finished_actions():
    doc = alert_doc()
    doc["timeline"] = [e for e in doc["timeline"] if e["kind"] != "action_finished"]
    ctx, calls, _ = fake_ctx([], doc)
    assert "skipped" in runbooks.propose({"alert_id": "a-1"}, ctx) and calls == []


def test_propose_caps_the_event_and_results_sent_to_the_model():
    doc = alert_doc()
    doc["alert"]["sample_event"]["raw"]["trace"] = "x" * 10000
    doc["timeline"][-2]["data"]["action_run"]["result"] = {"body": "y" * 2000}
    retry = {"text": "Retry the workflow", "action": "retry_workflow", "params": {"workflowId": "{{workflowId}}"}}
    ctx, calls, replies = fake_ctx([{"title": "WORKFLOW_FAILED in prod", "steps": [retry]}], doc)
    assert runbooks.propose({"alert_id": "a-1"}, ctx) == {"proposal_id": "prop-1"} and replies[0].startswith("Accepted")
    user = calls[0][2]
    assert "x" * 4000 not in user and "y" * 501 not in user and user.count("[truncated: ") == 2


def test_propose_from_the_fixture_timeline():
    check = {"text": "Check counterparty-service health", "action": "http_get",
             "params": {"url": "http://counterparty-service/health"}}
    retry = {"text": "Retry the workflow", "action": "retry_workflow", "params": {"workflowId": "{{workflowId}}"}}
    ctx, calls, replies = fake_ctx([
        {"title": "WORKFLOW_FAILED in prod", "steps": [check, {"text": "Restart it", "action": "restart_service"}]},
        {"title": "WORKFLOW_FAILED in prod", "steps": [check, {**retry, "params": {"workflowId": "{{workflow.id}}"}}]},
        {"title": "WORKFLOW_FAILED in prod", "steps": [{"text": "Acknowledge the alert"}, check, retry]}], alert_doc())
    assert runbooks.propose({"alert_id": "a-1"}, ctx) == {"proposal_id": "prop-1"}
    assert "'restart_service' was not run" in replies[0]
    assert "{{workflow.id}} does not resolve" in replies[1] and replies[2].startswith("Accepted")
    agent, post = calls
    assert "propose_runbook" in agent[1] and '"action": "retry_workflow"' in agent[2]
    assert post[:2] == ("POST", "/alerts/a-1/proposals") and post[2]["kind"] == "runbook"
    body = post[2]["body"]
    assert body["team_id"] == "settlements" and body["title"] == "WORKFLOW_FAILED in prod"
    assert body["canonical"]["id"] == "workflow-failed-prod"
    assert body["canonical"]["matches"] == {"all": [{"field": "type", "op": "eq", "value": "WORKFLOW_FAILED"},
                                                    {"field": "env", "op": "eq", "value": "prod"}]}
    assert [s.get("approval") for s in body["canonical"]["steps"]] == [None, "none", "required"]
