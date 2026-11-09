#!/usr/bin/env bash
# Builds Snip and installs it as ~/Applications/Snip.app (no admin rights). Re-run to update.
#   SNIP_APP_DIR=<dir>          install somewhere else (default ~/Applications)
#   SNIP_NO_OPEN=1              don't launch after installing
#   SNIP_SIGN_IDENTITY=<name>   codesign identity (default: "Snip Local Signing" if present, else ad-hoc)
set -euo pipefail
cd "$(dirname "$0")"

APP_DIR="${SNIP_APP_DIR:-$HOME/Applications}"
APP="$APP_DIR/Snip.app"

echo "==> Building (release, $(uname -m))"
swift build -c release
BIN="$(swift build -c release --show-bin-path)/Snip"

echo "==> Stopping a running Snip"
pkill -x Snip 2>/dev/null || true

echo "==> Assembling $APP"
mkdir -p "$APP_DIR"
rm -rf "$APP"
mkdir -p "$APP/Contents/MacOS" "$APP/Contents/Resources"
cp "$BIN" "$APP/Contents/MacOS/Snip"
cp Resources/Info.plist "$APP/Contents/Info.plist"
./scripts/make-icon.sh "$APP/Contents/Resources/Snip.icns" || echo "warning: icon generation failed (continuing without an icon)"

IDENTITY="${SNIP_SIGN_IDENTITY:-}"
if [ -z "$IDENTITY" ] && security find-identity -p codesigning 2>/dev/null | grep -q "Snip Local Signing"; then
    IDENTITY="Snip Local Signing"
fi
if [ -n "$IDENTITY" ]; then
    echo "==> Signing with '$IDENTITY'"
    codesign --force --deep --sign "$IDENTITY" "$APP"
else
    echo "==> Ad-hoc signing (macOS may ask for Screen Recording permission again after each rebuild; see README)"
    codesign --force --deep --sign - "$APP"
fi
codesign -dv "$APP" 2>&1 | sed 's/^/    /'

if [ -z "${SNIP_NO_OPEN:-}" ]; then
    echo "==> Launching"
    open "$APP"
fi
echo "Installed $APP"
echo "Log: ~/Library/Logs/Snip/snip.log   Diagnostics: \"$APP/Contents/MacOS/Snip\" --diagnostics"
