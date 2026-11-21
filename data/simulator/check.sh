#!/usr/bin/env bash
# Fast checks: lint + unit tests. No network in the tests (installing packages may use it). No Kafka needed.
set -euo pipefail
cd "$(dirname "$0")"

# Own venv, set up once; reinstalled only when pyproject.toml changes.
if [ ! -x .venv/bin/python ]; then
  python3 -m venv .venv
fi
stamp=".venv/.installed-$(cksum pyproject.toml | cut -d' ' -f1)"
if [ ! -f "$stamp" ]; then
  .venv/bin/python -m pip install -q --upgrade pip
  .venv/bin/python -m pip install -q -e '.[dev]'
  rm -f .venv/.installed-*
  touch "$stamp"
fi

.venv/bin/ruff check .
.venv/bin/python -m pytest -q
