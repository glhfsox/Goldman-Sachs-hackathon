"""Job kinds `runbook` (convert a draft to the canonical form) and `proposal` (draft a runbook update from a
resolved alert's timeline). Placeholders: the tools are written next."""

from .agent import JobFailed


def convert(job_input: dict, ctx) -> dict:
    raise JobFailed("not implemented")


def propose(job_input: dict, ctx) -> dict:
    raise JobFailed("not implemented")
