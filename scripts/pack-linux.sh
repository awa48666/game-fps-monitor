#!/usr/bin/env bash
# GameFPS release packer (Linux cross-build)
#
# Mirrors publish.ps1, but runs on Linux: publishes the WinForms backend for
# win-x64 (self-contained, so the target machine needs nothing installed) and
# assembles the Electron runtime for win32-x64 next to it.
#
# Usage:
#   scripts/pack-linux.sh                 # build into .build/GameFPS
#   scripts/pack-linux.sh --to-share      # build, then sync into /mnt/hgfs/fps/GameFPS
#   OUT_DIR=/path scripts/pack-linux.sh   # custom output directory
#
# Layout produced (identical to publish.ps1):
#   GameFPS.exe            <- double click (one UAC prompt)
#   app/                   <- Electron frontend
#     electron/electron.exe
#   *.dll ...
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
OUT_DIR="${OUT_DIR:-$ROOT/.build/GameFPS}"
TO_SHARE=0
[ "${1:-}" = "--to-share" ] && TO_SHARE=1

DOTNET="${DOTNET:-$HOME/.dotnet/dotnet}"
ELECTRON_VER="$(node -p "require('$ROOT/app/node_modules/electron/package.json').version")"
CACHE_DIR="${XDG_CACHE_HOME:-$HOME/.cache}/gamefps-pack"
ZIP="$CACHE_DIR/electron-v${ELECTRON_VER}-win32-x64.zip"
SHARE_DIR="${SHARE_DIR:-/mnt/hgfs/fps}"

say() { printf '==> %s\n' "$*"; }

say "electron $ELECTRON_VER (win32-x64), output $OUT_DIR"

# ---- [0/5] Windows Electron runtime (cached, ~120 MB download once) ----------
if [ ! -f "$ZIP" ]; then
    mkdir -p "$CACHE_DIR"
    say "[0/5] downloading electron win32-x64"
    curl -fL --http1.1 --retry 3 -o "$ZIP.part" \
        "https://npmmirror.com/mirrors/electron/${ELECTRON_VER}/electron-v${ELECTRON_VER}-win32-x64.zip"
    mv "$ZIP.part" "$ZIP"
fi

rm -rf "$OUT_DIR"
mkdir -p "$OUT_DIR"

# ---- [1/5] publish C# backend (self-contained) ------------------------------
say "[1/5] publish backend (win-x64, self-contained)"
"$DOTNET" publish "$ROOT/backend/GameFPS.Backend" \
    -c Release -r win-x64 --self-contained true --nologo \
    -o "$OUT_DIR" -p:EnableWindowsTargeting=true -v quiet

# ---- [2/5] frontend ---------------------------------------------------------
say "[2/5] copy frontend"
APP_DST="$OUT_DIR/app"
mkdir -p "$APP_DST"
cp -f "$ROOT/app/main.js" "$ROOT/app/preload.js" "$ROOT/app/package.json" "$APP_DST/"
rm -rf "$APP_DST/src"
cp -r "$ROOT/app/src" "$APP_DST/src"

# ---- [3/5] Electron runtime -------------------------------------------------
say "[3/5] unpack electron runtime"
mkdir -p "$APP_DST/electron"
unzip -q -o "$ZIP" -d "$APP_DST/electron"
chmod -R u+rw "$APP_DST/electron"

# ---- [4/5] trim locales -----------------------------------------------------
say "[4/5] trim locales"
if [ -d "$APP_DST/electron/locales" ]; then
    find "$APP_DST/electron/locales" -name '*.pak' \
        ! -name 'zh-CN.pak' ! -name 'en-US.pak' -delete
fi

# ---- [5/5] slimming ---------------------------------------------------------
# Every item below was verified to keep the app launchable. Do NOT remove
# dxcompiler.dll / dxil.dll / d3dcompiler_47.dll / resources.pak / icudtl.dat /
# v8 snapshot / *.pak in electron root / resources/default_app.asar --
# electron.exe exits with code 1 without them.
say "[5/5] slimming"
rm -f "$APP_DST/electron/LICENSES.chromium.html" \
      "$APP_DST/electron/vk_swiftshader.dll" \
      "$APP_DST/electron/vk_swiftshader_icd.json" \
      "$APP_DST/electron/vulkan-1.dll" \
      "$OUT_DIR/System.Windows.Forms.Design.dll" \
      "$OUT_DIR/Microsoft.DiaSymReader.Native.amd64.dll"
rm -rf "$OUT_DIR/arm64" "$OUT_DIR/x86"
find "$OUT_DIR" -maxdepth 1 -type d -regextype posix-extended \
     -regex '.*/[a-z]{2}(-[A-Za-z]{2,4})?' \
     ! -name 'zh-Hans' ! -name 'zh-Hant' ! -name 'zh-CN' ! -name 'zh-TW' \
     -exec rm -rf {} +

VERSION="$(cd "$ROOT" && git describe --tags --abbrev=0 2>/dev/null || echo dev)"
COMMIT="$(cd "$ROOT" && git rev-parse --short HEAD 2>/dev/null || echo unknown)"
cat > "$OUT_DIR/版本.txt" <<EOF
GameFPS $VERSION ($COMMIT)
构建时间 $(date '+%Y-%m-%d %H:%M:%S')
双击 GameFPS.exe 运行（采集需要管理员权限，会弹一次 UAC）
EOF

SIZE=$(du -sm "$OUT_DIR" | cut -f1)
say "done: $OUT_DIR  (${SIZE} MB, $VERSION)"

# ---- optional: sync into the host shared folder -----------------------------
if [ "$TO_SHARE" = 1 ]; then
    [ -d "$SHARE_DIR" ] || { echo "share dir missing: $SHARE_DIR" >&2; exit 1; }
    say "sync -> $SHARE_DIR/GameFPS"
    rm -rf "$SHARE_DIR/GameFPS.new"
    cp -r "$OUT_DIR" "$SHARE_DIR/GameFPS.new"
    rm -rf "$SHARE_DIR/GameFPS"
    mv "$SHARE_DIR/GameFPS.new" "$SHARE_DIR/GameFPS"
    say "synced: $SHARE_DIR/GameFPS/GameFPS.exe ($(du -sm "$SHARE_DIR/GameFPS" | cut -f1) MB)"
fi
