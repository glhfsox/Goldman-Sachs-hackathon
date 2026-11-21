"""run_agent against the real SDK client with a mock HTTP transport returning scripted Messages: no network."""

import json

import anthropic
import httpx2
import pytest
from anthropic import beta_tool

from ai_worker import agent


def reply(stop_reason, content):
    return {"id": "msg_1", "type": "message", "role": "assistant", "model": "claude-opus-5-5", "content": content,
            "stop_reason": stop_reason, "stop_sequence": None, "usage": {"input_tokens": 10, "output_tokens": 5}}


def tool_use(i, value):
    return {"type": "tool_use", "id": f"toolu_{i}", "name": "submit", "input": {"value": value}}


@pytest.fixture
def scripted(monkeypatch):
    requests, replies = [], []

    def handle(request):
        requests.append({**json.loads(request.content), "_beta": request.headers.get("anthropic-beta")})
        return httpx2.Response(200, json=replies.pop(0))

    client = anthropic.Anthropic(api_key="test", max_retries=0,
                                 http_client=anthropic.DefaultHttpxClient(transport=httpx2.MockTransport(handle)))
    monkeypatch.setattr(agent, "_client", lambda: client)
    return requests, replies


@beta_tool
def submit(value: str) -> str:
    """Submit the answer.

    Args:
        value: The answer.
    """
    return "saved" if value == "good" else "error: value must be good"


def test_loop_feeds_tool_results_back_until_end_turn(scripted):
    requests, replies = scripted
    replies += [reply("tool_use", [tool_use(1, "bad")]), reply("tool_use", [tool_use(2, "good")]),
                reply("end_turn", [{"type": "text", "text": "done"}])]
    agent.run_agent("system text", "user text", submit)

    assert len(requests) == 3
    first = requests[0]
    assert first["model"] == "claude-opus-5-5" and first["thinking"] == {"type": "adaptive"}
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
        agent.run_agent("s", "u", submit)


def test_iteration_cap_stops_the_loop(scripted):
    requests, replies = scripted
    replies += [reply("tool_use", [tool_use(i, "bad")]) for i in range(agent.MAX_ITERATIONS)]
    agent.run_agent("s", "u", submit)
    assert len(requests) == agent.MAX_ITERATIONS
