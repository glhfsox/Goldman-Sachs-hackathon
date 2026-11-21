#!/usr/bin/env bash
# Fast checks for backend/ai-worker: unit tests, no network in the tests themselves.
set -euo pipefail
cd "$(dirname "$0")"
command -v uv >/dev/null || pip install uv
uv run pytest -q
