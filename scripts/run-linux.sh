#!/usr/bin/env bash
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
if [ -d "$HOME/.dotnet" ]; then
    export DOTNET_ROOT="${DOTNET_ROOT:-$HOME/.dotnet}"
    export PATH="$DOTNET_ROOT:$PATH"
fi

BIN="$ROOT/src/StrongholdAssistant.Linux/bin/Release/net8.0/StrongholdAssistant"
if [ ! -x "$BIN" ]; then
    echo "Binary not found at $BIN; running build first..." >&2
    "$ROOT/scripts/build-linux.sh"
fi

exec "$BIN" "$@"
