#!/usr/bin/env bash
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
if [ -d "$HOME/.dotnet" ]; then
    export DOTNET_ROOT="${DOTNET_ROOT:-$HOME/.dotnet}"
    export PATH="$DOTNET_ROOT:$PATH"
fi

# Make system CUDA libraries (Arch /opt/cuda/lib64, Ubuntu /usr/local/cuda/lib64) visible to ONNX Runtime CUDA EP.
for cuda_dir in "/opt/cuda/lib64" "/usr/local/cuda/lib64"; do
    if [ -d "$cuda_dir" ]; then
        export LD_LIBRARY_PATH="$cuda_dir${LD_LIBRARY_PATH:+:$LD_LIBRARY_PATH}"
    fi
done

BIN="$ROOT/src/StrongholdAssistant.Linux/bin/Release/net8.0/StrongholdAssistant"
if [ ! -x "$BIN" ]; then
    echo "Binary not found at $BIN; running build first..." >&2
    "$ROOT/scripts/build-linux.sh"
fi

exec "$BIN" "$@"
