#!/usr/bin/env bash
# Fast checks: type check + unit tests (no network in the tests; installing packages may use it).
set -euo pipefail
cd "$(dirname "$0")"

# Install once, and again when the lockfile changes.
stamp="node_modules/.installed-$(cksum package-lock.json | cut -d' ' -f1)"
if [ ! -f "$stamp" ]; then
  npm ci --no-audit --no-fund
  touch "$stamp"
fi

npm run --silent typecheck
npm test --silent
