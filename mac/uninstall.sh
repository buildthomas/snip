#!/usr/bin/env bash
# Removes Snip completely: the app, Start at login, settings, logs, unsaved recordings and its permission entries.
set -uo pipefail
APP="${SNIP_APP_DIR:-$HOME/Applications}/Snip.app"

if [ -x "$APP/Contents/MacOS/Snip" ]; then
    pkill -x Snip 2>/dev/null || true
    "$APP/Contents/MacOS/Snip" --unregister-login || true
fi
pkill -x Snip 2>/dev/null || true
rm -rf "$APP"
defaults delete com.buildthomas.snip 2>/dev/null || true
rm -rf "$HOME/Library/Logs/Snip"
TMP="$(getconf DARWIN_USER_TEMP_DIR 2>/dev/null || true)"
[ -n "$TMP" ] && rm -rf "${TMP%/}/Snip"
tccutil reset ScreenCapture com.buildthomas.snip 2>/dev/null || true
tccutil reset Microphone com.buildthomas.snip 2>/dev/null || true
echo "Snip removed."
