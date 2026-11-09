#!/usr/bin/env bash
# Builds an .icns from the repo's docs/logo.png (same purple scissors as Windows). Usage: make-icon.sh <out.icns>
set -euo pipefail
OUT="$1"
SRC="$(cd "$(dirname "$0")/../.." && pwd)/docs/logo.png"
WORK="$(mktemp -d)"
SET="$WORK/Snip.iconset"
mkdir -p "$SET"
for s in 16 32 128 256 512; do
    sips -z "$s" "$s" "$SRC" --out "$SET/icon_${s}x${s}.png" >/dev/null
    d=$((s * 2))
    sips -z "$d" "$d" "$SRC" --out "$SET/icon_${s}x${s}@2x.png" >/dev/null
done
iconutil -c icns "$SET" -o "$OUT"
rm -rf "$WORK"
