"""One Claude tool-use loop with exactly one tool (Anthropic Python SDK Tool Runner, beta).

The tool's handler validates what the model submits and returns its errors as text, so the model can fix them and
call the tool again; the handler saves the result itself once it is valid.
"""

import functools
import logging
import os

import anthropic

log = logging.getLogger("ai_worker.agent")
MAX_ITERATIONS = 6  # model replies per job
MAX_TOKENS = 16000  # thinking counts toward it; below the SDK's non-streaming limit
TIMEOUT_S = 120.0  # per request; the SDK retries timeouts, 429 and 5xx twice


class JobFailed(Exception):
    """A permanent failure: the job is marked `failed` at once, no retry."""


@functools.cache
def _client() -> anthropic.Anthropic:
    return anthropic.Anthropic(timeout=TIMEOUT_S, max_retries=2)  # key from ANTHROPIC_API_KEY


def run_agent(system: str, user: str, tool) -> None:
    """Run the loop with only `tool` (a `@beta_tool` function) until the model stops or MAX_ITERATIONS replies.

    Returns normally on `end_turn`, and on `tool_use` when the iteration cap ends the loop: whether the tool saved a
    valid result is the handler's to check. Raises JobFailed on a refusal or a truncated reply.
    """
    runner = _client().beta.messages.tool_runner(
        model=os.environ.get("ANTHROPIC_MODEL") or "claude-opus-5-5",
        max_tokens=MAX_TOKENS,
        system=system,
        messages=[{"role": "user", "content": user}],
        tools=[tool],  # no tool_choice: this model rejects forced tool use, the prompt names the tool
        thinking={"type": "adaptive"},
        output_config={"effort": "medium"},
        betas=["server-side-fallback-2026-07-01"],
        fallbacks="default",  # a classifier decline is re-run server-side on Anthropic's recommended model
        max_iterations=MAX_ITERATIONS,
    )
    final = runner.until_done()
    log.info("agent stopped: stop_reason=%s model=%s", final.stop_reason, final.model)
    if final.stop_reason == "refusal":
        category = final.stop_details.category if final.stop_details else None
        raise JobFailed(f"the model declined (category {category})")
    if final.stop_reason not in ("end_turn", "tool_use"):
        raise JobFailed(f"the model stopped with stop_reason {final.stop_reason}")
    if final.stop_reason == "tool_use":
        log.warning("agent hit the cap of %d replies", MAX_ITERATIONS)
