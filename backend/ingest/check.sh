#!/usr/bin/env bash
# Fast checks: build and unit tests. Tests that need containers (Testcontainers) are added with the code that uses them.
set -euo pipefail
cd "$(dirname "$0")"

command -v dotnet > /dev/null 2>&1 || { echo "dotnet SDK 10 not found"; exit 1; }

dotnet build Ingest.slnx --nologo -v q
dotnet test Ingest.slnx --nologo --no-build
