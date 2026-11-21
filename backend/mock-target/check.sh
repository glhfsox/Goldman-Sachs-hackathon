#!/usr/bin/env bash
# mock-target fast checks: build (warnings as errors) and tests. In-memory only, no network beyond the test host.
set -euo pipefail
cd "$(dirname "$0")"

# CI runners may not ship .NET 10 yet; install it once into ~/.dotnet.
if ! command -v dotnet >/dev/null 2>&1 || ! dotnet --list-sdks | grep -q '^10\.'; then
  curl -sSL https://dot.net/v1/dotnet-install.sh -o /tmp/dotnet-install.sh
  bash /tmp/dotnet-install.sh --channel 10.0 --install-dir "$HOME/.dotnet" >/dev/null
  export DOTNET_ROOT="$HOME/.dotnet"
  export PATH="$HOME/.dotnet:$PATH"
fi

export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1
dotnet build MockTarget.slnx -warnaserror -v q -nologo
dotnet test MockTarget.slnx --no-build -v q -nologo
