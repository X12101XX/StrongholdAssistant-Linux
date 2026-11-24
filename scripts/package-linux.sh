#!/usr/bin/env bash
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
export DOTNET_ROOT="${DOTNET_ROOT:-$HOME/.dotnet}"
export PATH="$DOTNET_ROOT:$PATH"

VERSION="${1:-1.3.8-linux.1}"
ARCH="linux-x64"
PKG_NAME="StrongholdAssistant-${VERSION}-${ARCH}"
DIST_DIR="$ROOT/dist"
STAGE_DIR="$DIST_DIR/$PKG_NAME"
ARCHIVE="$DIST_DIR/${PKG_NAME}.tar.gz"

echo "==> Packaging $PKG_NAME"

echo "==> 1/4 Building native Wayland / GTK helpers..."
make -C "$ROOT/src/native" wayland stronghold-overlay stronghold-webview

echo "==> 2/4 Building Svelte UI (src/ui)..."
if [ ! -d "$ROOT/src/ui/node_modules" ]; then
    npm --prefix "$ROOT/src/ui" install --no-audit --no-fund
fi
npm --prefix "$ROOT/src/ui" run build

echo "==> 3/4 Publishing self-contained .NET 8 host ($ARCH)..."
rm -rf "$STAGE_DIR" "$ARCHIVE" "${ARCHIVE}.sha256"
mkdir -p "$STAGE_DIR"

dotnet publish "$ROOT/src/StrongholdAssistant.Linux/StrongholdAssistant.Linux.csproj" \
    -c Release \
    -r "$ARCH" \
    --self-contained true \
    -p:ReleaseBuild=true \
    -p:DebugType=None \
    -p:DebugSymbols=false \
    -o "$STAGE_DIR/app"

# Prune non-linux-x64 runtime binaries (Maa.Framework bundles win/osx/arm64)
if [ -d "$STAGE_DIR/app/runtimes" ]; then
    find "$STAGE_DIR/app/runtimes" -mindepth 1 -maxdepth 1 -type d ! -name "$ARCH" -exec rm -rf {} +
fi

# Deduplicate identical libSkiaSharp.so copy via symlink
if [ -f "$STAGE_DIR/app/libSkiaSharp.so" ] && [ -f "$STAGE_DIR/app/libSkiaSharp.so.116.0.0" ]; then
    if cmp -s "$STAGE_DIR/app/libSkiaSharp.so" "$STAGE_DIR/app/libSkiaSharp.so.116.0.0"; then
        ln -sf "libSkiaSharp.so.116.0.0" "$STAGE_DIR/app/libSkiaSharp.so"
    fi
fi

# Ensure native helpers are executable in app/
chmod +x "$STAGE_DIR/app/StrongholdAssistant" \
         "$STAGE_DIR/app/stronghold-overlay" \
         "$STAGE_DIR/app/stronghold-webview"

# Top-level launcher script (matches PackageLayout: app/ -> parent Root)
cat > "$STAGE_DIR/stronghold-assistant" <<'EOF'
#!/usr/bin/env bash
set -euo pipefail
DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
export LD_LIBRARY_PATH="$DIR/app:$DIR/app/runtimes/linux-x64/native:${LD_LIBRARY_PATH:-}"
exec "$DIR/app/StrongholdAssistant" "$@"
EOF
chmod +x "$STAGE_DIR/stronghold-assistant"

# Copy docs and license files required by Apache-2.0
cp "$ROOT/LICENSE" "$ROOT/NOTICE" "$ROOT/README.linux.md" "$STAGE_DIR/"
[ -f "$ROOT/README.md" ] && cp "$ROOT/README.md" "$STAGE_DIR/"

# Strip any accidental .pdb files to keep tarball lean
find "$STAGE_DIR" -name '*.pdb' -delete

echo "==> 4/4 Creating tar.gz archive and SHA256 checksum..."
tar -C "$DIST_DIR" -czf "$ARCHIVE" "$PKG_NAME"
(cd "$DIST_DIR" && sha256sum "$(basename "$ARCHIVE")" > "$(basename "$ARCHIVE").sha256")

ls -lh "$ARCHIVE" "${ARCHIVE}.sha256"
cat "${ARCHIVE}.sha256"
