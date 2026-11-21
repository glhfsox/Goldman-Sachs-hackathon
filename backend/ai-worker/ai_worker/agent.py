"""One Claude tool-use loop with exactly one tool: a LangGraph graph (model node <-> tool node) with a Postgres
checkpoint after every node, thread id = the job id, so a retried or crashed job resumes from its last checkpoint
instead of paying for every model call again.

The tool's handler validates what the model submits and returns its errors as text, so the model can fix them and
call the tool again; the handler saves the result itself once it is valid.
"""

import functools
import logging
import operator
import os
from typing import Annotated, TypedDict

import anthropic
import psycopg
from anthropic.lib.tools import ToolError
from langgraph.checkpoint.postgres import PostgresSaver
from langgraph.graph import END, START, StateGraph
from psycopg.rows import dict_row

log = logging.getLogger("ai_worker.agent")
MAX_ITERATIONS = 6  # model replies per job
MAX_TOKENS = 16000  # thinking counts toward it; below the SDK's non-streaming limit
TIMEOUT_S = 120.0  # per request; the SDK retries timeouts, connection errors, 408, 409, 429 and 5xx twice
CACHE = {"type": "ephemeral"}  # 5-minute prompt cache: the next call of a job, or its resume, reads the prefix
PRICES = {  # USD per million tokens: input, output, cache read, cache write (5 min); another model's cost is null
    "claude-sonnet-5-5": (2.0, 10.0, 0.10, 2.50),
    "claude-opus-5-5": (4.0, 20.0, 0.20, 5.00),
}


class JobFailed(Exception):
    """A permanent failure: the job is marked `failed` at once, no retry."""


class State(TypedDict):
    messages: Annotated[list[dict], operator.add]  # the conversation as sent to the API, blocks replayed unchanged
    replies: int
    input_tokens: int
    output_tokens: int
    stop_reason: str | None
    model: str | None
    refusal: str | None  # stop_details.category of a refusal


@functools.cache
def _client() -> anthropic.Anthropic:
    return anthropic.Anthropic(timeout=TIMEOUT_S, max_retries=2)  # key from ANTHROPIC_API_KEY


@functools.cache
def _saver() -> PostgresSaver:
    """Checkpoints in the ai-worker's own database; main() creates the tables (`setup()`) under the migration lock."""
    return PostgresSaver(psycopg.connect(os.environ["DATABASE_URL"], autocommit=True, prepare_threshold=0,
                                         row_factory=dict_row))


def _run_tool(tool, block: dict) -> dict:
    """One tool call -> its tool_result, as the SDK's Tool Runner builds it: an exception becomes error text."""
    result = {"type": "tool_result", "tool_use_id": block["id"]}
    if block["name"] != tool.name:
        return result | {"content": f"Error: Tool '{block['name']}' not found", "is_error": True}
    try:
        return result | {"content": tool.call(block["input"])}
    except ToolError as e:
        return result | {"content": e.content, "is_error": True}
    except (psycopg.OperationalError, psycopg.InterfaceError):
        raise  # our own database is down: not the model's to fix; the worker reconnects and the job resumes
    except Exception as e:
        log.exception("tool %s raised", tool.name)
        return result | {"content": repr(e), "is_error": True}


def new_metrics() -> dict:
    """What one attempt of a job spent; `run_agent` fills it, the worker writes it to the metrics file."""
    return {"model": None, "model_calls": 0, "prior_model_calls": 0, "resumed_from_checkpoint": False,
            "tool_calls": {}, "tool_errors": 0, "input_tokens": 0, "output_tokens": 0, "cache_read_input_tokens": 0,
            "cache_creation_input_tokens": 0, "cost_usd": 0.0, "sdk_retries": 0, "stop_reason": None}


def _count(m: dict, reply, retries: int) -> None:
    u = reply.usage
    read, write = u.cache_read_input_tokens or 0, u.cache_creation_input_tokens or 0
    p = PRICES.get(reply.model)
    m["cost_usd"] = None if p is None or m["cost_usd"] is None else \
        m["cost_usd"] + (u.input_tokens * p[0] + u.output_tokens * p[1] + read * p[2] + write * p[3]) / 1e6
    m["model_calls"] += 1
    m["sdk_retries"] += retries
    m["input_tokens"] += u.input_tokens
    m["output_tokens"] += u.output_tokens
    m["cache_read_input_tokens"] += read
    m["cache_creation_input_tokens"] += write
    m["model"], m["stop_reason"] = reply.model, reply.stop_reason


def _graph(system: str, tool, m: dict):
    client, tools = _client(), [tool.to_dict()]

    def model(state: State) -> dict:
        raw = client.beta.messages.with_raw_response.create(
            model=os.environ.get("ANTHROPIC_MODEL") or "claude-sonnet-5-5",
            max_tokens=MAX_TOKENS,
            system=[{"type": "text", "text": system, "cache_control": CACHE}],  # caches tools + system
            cache_control=CACHE,  # automatic breakpoint on the end of the growing conversation
            messages=state["messages"],
            tools=tools,  # no tool_choice: Sonnet 5.5 rejects forced tool use, the prompt names the tool
            thinking={"type": "adaptive"},
            output_config={"effort": "medium"},
            betas=["server-side-fallback-2026-07-01"],
            fallbacks="default",  # a classifier decline is re-run server-side on Anthropic's recommended model
        )
        reply = raw.parse()
        _count(m, reply, raw.retries_taken)
        return {"messages": [{"role": "assistant", "content": [b.to_dict(mode="json") for b in reply.content]}],
                "replies": state["replies"] + 1, "input_tokens": state["input_tokens"] + reply.usage.input_tokens,
                "output_tokens": state["output_tokens"] + reply.usage.output_tokens,
                "stop_reason": reply.stop_reason, "model": reply.model,
                "refusal": reply.stop_details.category if reply.stop_reason == "refusal" and reply.stop_details else None}

    def run_tools(state: State) -> dict:
        calls = [b for b in state["messages"][-1]["content"] if b["type"] == "tool_use"]
        results = [_run_tool(tool, b) for b in calls]
        for b, r in zip(calls, results):
            m["tool_calls"][b["name"]] = m["tool_calls"].get(b["name"], 0) + 1
            m["tool_errors"] += bool(r.get("is_error"))
        return {"messages": [{"role": "user", "content": results}]}

    g = StateGraph(State)
    g.add_node("model", model)
    g.add_node("tools", run_tools)
    g.add_edge(START, "model")
    g.add_conditional_edges("model", lambda s: "tools" if s["stop_reason"] == "tool_use" and any(
        b["type"] == "tool_use" for b in s["messages"][-1]["content"]) else END, ["tools", END])
    g.add_conditional_edges("tools", lambda s: "model" if s["replies"] < MAX_ITERATIONS else END, ["model", END])
    return g.compile(checkpointer=_saver())


def run_agent(system: str, user: str, tool, *, thread_id: str, metrics: dict | None = None) -> None:
    """Run the loop with only `tool` (a `@beta_tool` function) until the model stops or MAX_ITERATIONS replies,
    resuming thread `thread_id` from its checkpoint when it has one. `metrics` (from `new_metrics`) gets this run's
    model calls, tokens, cost and tool calls.

    Returns normally on `end_turn`, and on `tool_use` when the iteration cap ends the loop: whether the tool saved a
    valid result is the handler's to check. Raises JobFailed on a refusal or a truncated reply.
    """
    m = new_metrics() if metrics is None else metrics
    graph, config = _graph(system, tool, m), {"configurable": {"thread_id": thread_id}}
    before = graph.get_state(config).values
    m["resumed_from_checkpoint"], m["prior_model_calls"] = bool(before), (before or {}).get("replies", 0)
    if before:
        # the handler keeps what it saved in memory: re-run the calls the checkpoint already answered (validation,
        # and an idempotent PUT for a mapping), then go on from the checkpoint
        msgs = before["messages"]
        for i, m in enumerate(msgs):
            if m["role"] == "assistant" and i + 1 < len(msgs):
                for b in m["content"]:
                    if b["type"] == "tool_use":
                        _run_tool(tool, b)
        log.info("resuming %s from its checkpoint after %d replies", thread_id, before["replies"])
        final = graph.invoke(None, config, durability="sync")
    else:
        final = graph.invoke({"messages": [{"role": "user", "content": user}], "replies": 0, "input_tokens": 0,
                              "output_tokens": 0, "stop_reason": None, "model": None, "refusal": None}, config,
                             durability="sync")  # each checkpoint is written (or raises) before the next node runs
    log.info("agent stopped: stop_reason=%s model=%s replies=%d (this run %d) input_tokens=%d output_tokens=%d",
             final["stop_reason"], final["model"], final["replies"], final["replies"] - (before or {}).get("replies", 0),
             final["input_tokens"], final["output_tokens"])
    if final["stop_reason"] == "refusal":
        raise JobFailed(f"the model declined (category {final['refusal']})")
    if final["stop_reason"] not in ("end_turn", "tool_use"):
        raise JobFailed(f"the model stopped with stop_reason {final['stop_reason']}")
    if final["stop_reason"] == "tool_use":
        log.warning("agent hit the cap of %d replies", MAX_ITERATIONS)


def reset() -> None:
    """Drop the cached checkpoint connection after a database error; the next job opens a new one."""
    if _saver.cache_info().currsize:
        _saver().conn.close()
    _saver.cache_clear()
