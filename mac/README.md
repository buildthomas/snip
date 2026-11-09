# Snip on macOS: build and platform details

How to use Snip, its shortcuts and tools are in the main [README](../README.md); this page covers what's specific to
the macOS build. Agents working here: read [AGENTS.md](AGENTS.md).

The macOS version is newer than the Windows one and still experimental: it builds, passes its unit tests and packages
on CI (Apple silicon), and is being verified on real hardware.

## Requirements

macOS 14 (Sonoma) or newer, Apple silicon or Intel, Xcode command-line tools (`xcode-select --install`). No Apple
Developer account or admin rights.

## Install, update, remove

```bash
git clone https://github.com/buildthomas/snip && cd snip/mac
./scripts/create-signing-identity.sh   # optional, once: keeps Screen Recording permission across rebuilds
./install.sh                           # build, assemble ~/Applications/Snip.app, sign, launch
git pull && ./install.sh               # update
./uninstall.sh                         # remove app, Start at login, settings, logs and the permission entry
```

`install.sh` options: `SNIP_APP_DIR=<dir>` (default `~/Applications`), `SNIP_NO_OPEN=1` (don't launch),
`SNIP_SIGN_IDENTITY=<name>`. Without a signing identity the app is ad-hoc signed, and macOS may ask for Screen
Recording permission again after every rebuild.

## Permissions

- **Screen Recording** (System Settings > Privacy & Security > Screen Recording) is required for capture. Snip asks on
  the first capture; quit and reopen Snip after granting it.
- No Accessibility or Input Monitoring permission: the hotkey uses Carbon's `RegisterEventHotKey`.

## Command line

All flags work on `~/Applications/Snip.app/Contents/MacOS/Snip`:

| Flag | Does |
|---|---|
| `--capture` | Take a screenshot (asks the running copy if there is one); also `open -a Snip --args --capture` |
| `--set-hotkey cmd+shift+s` | Set the global hotkey; a running copy switches right away |
| `--diagnostics` | Print macOS version, architecture, permission, hotkey, login item and display details |
| `--version` | Print the version |
| `--unregister-login` | Turn off Start at login (used by `uninstall.sh`) |

## Logs and settings

- Log: `~/Library/Logs/Snip/snip.log` (rotates at 1 MB). Debug logging is on by default while the port is
  experimental (menu: Debug logging; or `SNIP_DEBUG=1`). `SNIP_LOG_STDERR=1` mirrors it to the terminal; the unified
  log has it too: `log stream --predicate 'subsystem == "com.buildthomas.snip"'`.
- Settings: `defaults read com.buildthomas.snip`; reset with `defaults delete com.buildthomas.snip`.

## Known gaps

- A selection lives on one display (no selections spanning two monitors).
- The overlay repaints fully on every change; fine on typical displays, possibly heavy on very large ones.
- Not notarized; it's meant to be built from source on the Mac that runs it.
