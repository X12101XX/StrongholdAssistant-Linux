#!/usr/bin/env bash
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
export DOTNET_ROOT="${DOTNET_ROOT:-$HOME/.dotnet}"
export PATH="$DOTNET_ROOT:$PATH"

echo "==> 1/3 Building native Wayland / GTK helpers..."
make -C "$ROOT/src/native" wayland stronghold-overlay stronghold-webview

echo "==> 2/3 Building Svelte UI (src/ui)..."
if [ ! -d "$ROOT/src/ui/node_modules" ]; then
    npm --prefix "$ROOT/src/ui" install --no-audit --no-fund
fi
npm --prefix "$ROOT/src/ui" run build

echo "==> 3/3 Building StrongholdAssistant.Linux (.NET 8)..."
dotnet build "$ROOT/src/StrongholdAssistant.Linux/StrongholdAssistant.Linux.csproj" -c Release -p:ReleaseBuild=true

echo "Done! Launch with: $ROOT/scripts/run-linux.sh"
