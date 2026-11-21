#!/usr/bin/env bash
# Notifier fast checks: build (warnings as errors) and unit tests. No database, broker or network in the tests.
set -euo pipefail
cd "$(dirname "$0")"

# CI runners may not ship .NET 10 yet; install it once into ~/.dotnet.
if ! command -v dotnet >/dev/null 2>&1 || ! dotnet --list-sdks | grep -q '^10\.'; then
  curl -sSL https://dot.net/v1/dotnet-install.sh -o /tmp/dotnet-install.sh
  bash /tmp/dotnet-install.sh --channel 10.0 --install-dir "$HOME/.dotnet" >/dev/null
  # macOS kills a signed binary that was overwritten in place (cached signature): give the host a fresh inode.
  cp "$HOME/.dotnet/dotnet" "$HOME/.dotnet/dotnet.new" && mv -f "$HOME/.dotnet/dotnet.new" "$HOME/.dotnet/dotnet"
  export DOTNET_ROOT="$HOME/.dotnet"
  export PATH="$HOME/.dotnet:$PATH"
fi

export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1
dotnet build Notifier.slnx -warnaserror -v q -nologo
dotnet test Notifier.slnx --no-build -v q -nologo
