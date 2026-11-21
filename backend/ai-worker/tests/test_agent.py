"""run_agent against the real SDK client with a mock HTTP transport returning scripted Messages: no network."""

import json

import anthropic
import httpx2
import psycopg
import pytest
from anthropic import beta_tool
from langgraph.checkpoint.memory import InMemorySaver
from langgraph.checkpoint.postgres import PostgresSaver
from psycopg.rows import dict_row

from ai_worker import agent


def reply(stop_reason, content):
    return {"id": "msg_1", "type": "message", "role": "assistant", "model": "claude-sonnet-5-5", "content": content,
            "stop_reason": stop_reason, "stop_sequence": None, "usage": {"input_tokens": 10, "output_tokens": 5}}


def tool_use(i, value):
    return {"type": "tool_use", "id": f"toolu_{i}", "name": "submit", "input": {"value": value}}


@pytest.fixture
def scripted(monkeypatch):
    requests, replies = [], []

    def handle(request):
        requests.append({**json.loads(request.content), "_beta": request.headers.get("anthropic-beta")})
        r = replies.pop(0)
        return httpx2.Response(500, json={"type": "error", "error": {"type": "api_error", "message": "down"}}) \
            if r == 500 else httpx2.Response(200, json=r)

    client = anthropic.Anthropic(api_key="test", max_retries=0,
                                 http_client=anthropic.DefaultHttpxClient(transport=httpx2.MockTransport(handle)))
    monkeypatch.setattr(agent, "_client", lambda: client)
    saver = InMemorySaver()
    monkeypatch.setattr(agent, "_saver", lambda: saver)
    return requests, replies


seen = []


@beta_tool
def submit(value: str) -> str:
    """Submit the answer.

    Args:
        value: The answer.
    """
    seen.append(value)
    return "saved" if value == "good" else "error: value must be good"


def test_loop_feeds_tool_results_back_until_end_turn(scripted):
    requests, replies = scripted
    replies += [reply("tool_use", [tool_use(1, "bad")]), reply("tool_use", [tool_use(2, "good")]),
                reply("end_turn", [{"type": "text", "text": "done"}])]
    agent.run_agent("system text", "user text", submit, thread_id="j1")

    assert len(requests) == 3
    first = requests[0]
    assert first["model"] == "claude-sonnet-5-5" and first["thinking"] == {"type": "adaptive"}
    assert first["output_config"]["effort"] == "medium" and first["fallbacks"] == "default"
    assert "tool_choice" not in first and [t["name"] for t in first["tools"]] == ["submit"]
    assert "server-side-fallback-2026-07-01" in first["_beta"].split(",")
    assert first["system"] == "system text" and first["messages"] == [{"role": "user", "content": "user text"}]
    result = requests[1]["messages"][-1]["content"][0]
    assert result["type"] == "tool_result" and result["tool_use_id"] == "toolu_1"
    assert "value must be good" in json.dumps(result["content"])
    assert "saved" in json.dumps(requests[2]["messages"][-1]["content"][0]["content"])


def test_refusal_is_a_permanent_failure(scripted):
    _, replies = scripted
    replies.append({**reply("refusal", []), "stop_details": {"type": "refusal", "category": "cyber", "explanation": None}})
    with pytest.raises(agent.JobFailed, match="declined"):
        agent.run_agent("s", "u", submit, thread_id="j1")


def test_iteration_cap_stops_the_loop(scripted):
    requests, replies = scripted
    replies += [reply("tool_use", [tool_use(i, "bad")]) for i in range(agent.MAX_ITERATIONS)]
    agent.run_agent("s", "u", submit, thread_id="j1")
    assert len(requests) == agent.MAX_ITERATIONS


def test_a_rerun_resumes_from_the_checkpoint_and_forget_deletes_it(scripted, monkeypatch, db):
    requests, replies = scripted
    with psycopg.connect(db, autocommit=True, prepare_threshold=0, row_factory=dict_row) as conn:
        saver = PostgresSaver(conn)
        saver.setup()
        monkeypatch.setattr(agent, "_saver", lambda: saver)
        thinking = {"type": "thinking", "thinking": "", "signature": "sig-1"}
        replies += [reply("tool_use", [thinking, tool_use(1, "bad")]), reply("tool_use", [tool_use(2, "good")]), 500]
        with pytest.raises(anthropic.InternalServerError):  # the worker dies on the third model call
            agent.run_agent("s", "u", submit, thread_id="j1")
        assert len(requests) == 3

        seen.clear()
        replies.append(reply("end_turn", [{"type": "text", "text": "done"}]))
        agent.run_agent("s", "u", submit, thread_id="j1")
        assert len(requests) == 4  # one model call, not four
        assert seen == ["bad", "good"]  # the answered calls ran again, so the handler knows what it saved
        resumed = requests[3]["messages"]
        assert len(resumed) == 5 and resumed[1]["content"][0] == thinking

        agent.forget("j1")
        assert conn.execute("SELECT count(*) AS n FROM checkpoints WHERE thread_id = 'j1'").fetchone()["n"] == 0
