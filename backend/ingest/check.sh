#!/usr/bin/env bash
# Fast checks: build and unit tests (no network, no Docker).
# Container tests (Category=Integration, Testcontainers): RUN_INTEGRATION=1 ./check.sh
set -euo pipefail
cd "$(dirname "$0")"

command -v dotnet > /dev/null 2>&1 || { echo "dotnet SDK 10 not found"; exit 1; }

dotnet build Ingest.slnx --nologo -v q
if [ "${RUN_INTEGRATION:-0}" = "1" ]; then
  dotnet test Ingest.slnx --nologo --no-build
else
  dotnet test Ingest.slnx --nologo --no-build --filter "Category!=Integration"
fi
