# Agent notes: macOS port

Read the repo's [AGENTS.md](../AGENTS.md) first: its product principles and hard rules apply here too (no network,
no new runtime dependencies, never log typed text or screen content, secure redaction stays content-independent,
product decisions go to the owner). This file covers what's specific to `mac/`.

**Features land in both apps.** A new feature or behaviour change made here also goes into the Windows app
(`src/Snip`), and vice versa; if one side can't be done in the same PR, record the gap in the
[feature parity table](../AGENTS.md#feature-parity). Shared algorithms (`SnipCore` geometry and redaction) must stay
equivalent to their Windows counterparts, tests included.

## Status

Experimental: it builds, `SnipCore` passes its unit tests on GitHub Actions (`.github/workflows/mac.yml`, Apple
silicon `macos-latest` runner), and the app has been used on a real Apple silicon Mac. Less exercised: multi-display
and mixed-scale setups. Fix what you find here.

## Layout

| Path | What |
|---|---|
| `Sources/SnipCore` | Pure logic, no AppKit: geometry, redaction (port of the Windows algorithm). Unit tested. |
| `Sources/Snip/App.swift`, `main.swift` | Menu-bar app, single instance, CLI flags, Start at login (`SMAppService`) |
| `Sources/Snip/HotKey.swift`, `HotkeyRecorder.swift` | Carbon `RegisterEventHotKey` (no Accessibility permission); recorder window for Change hotkey |
| `Sources/SnipCore/HotkeySpec.swift` | Hotkey model: parses/prints `cmd+shift+s`, Carbon masks, validation. Unit tested. |
| `Sources/Snip/Capture.swift` | ScreenCaptureKit capture of every display, window rectangles for click-to-select |
| `Sources/Snip/Overlay.swift` | Per-display overlay window + view: selection, tools, bars, keyboard, copy/save |
| `Sources/Snip/Annotations.swift` | Shapes; `RedactMark` calls SnipCore for its pixels |
| `Sources/Snip/PixelBridge.swift` | CGImage <-> SnipCore pixel buffers, path masks |
| `Sources/Snip/Log.swift`, `Diagnostics.swift` | File log and the `--diagnostics` report |

Coordinates: each overlay view is flipped (origin top-left, y down) in the display's **points**; captured images are
in **pixels** (`DisplayCapture.scale` = pixels per point). Annotations store points; redaction converts to pixels.

## Build, test, install

```bash
cd mac
swift build            # debug build
swift test             # SnipCore unit tests
./install.sh           # release build -> ~/Applications/Snip.app, signed, launched
./uninstall.sh         # removes app, login item, settings, logs, permission entry
```

Install from a normal terminal session. Agent sandboxes can virtualize `~/Library` or `defaults`, which makes
Start at login, settings or permissions look broken when they aren't.

## Debugging without the UI

- **Log:** `~/Library/Logs/Snip/snip.log` (rotates at 1 MB to `snip.1.log`). Debug logging is on by default while the
  port is experimental (menu: Debug logging; or `SNIP_DEBUG=1`). It records startup, the diagnostics report,
  hotkey registration results, every capture with per-display timings and sizes, overlay show/close reasons, tool
  changes, redaction tile timings, copy/save results and all errors. `tail -f` it while testing.
- **Mirror to the terminal:** run the binary directly with `SNIP_LOG_STDERR=1 ~/Applications/Snip.app/Contents/MacOS/Snip`.
- **Unified log:** `log stream --predicate 'subsystem == "com.buildthomas.snip"'`.
- **Environment report:** `~/Applications/Snip.app/Contents/MacOS/Snip --diagnostics` prints macOS version,
  architecture, bundle path/id, Screen Recording permission, hotkey, login item status and every display's frame and
  scale. Paste it into bug reports.
- **Trigger a capture without the hotkey:** `open -a Snip --args --capture` (a second launch tells the running copy).
- **Settings:** `defaults read com.buildthomas.snip`; reset with `defaults delete com.buildthomas.snip`.
- **Hotkey from the CLI:** `Snip --set-hotkey cmd+shift+s` (the running copy re-registers; a conflict is logged).

Overlay commands (Undo, Redo, Copy, Paste, Select All, Save…) are items in the hidden main menu
(`AppDelegate.buildMainMenu`), not hard-coded keys, so the user's App Shortcuts apply. Keep it that way: add new
commands as menu items with standard titles.

## Verification checklist (run on a real Mac, tick in the PR)

1. `./install.sh` succeeds; menu-bar icon appears; `--diagnostics` shows the bundle id and arch `arm64`.
2. First capture prompts for Screen Recording; after granting and relaunching, `--diagnostics` says granted.
3. The hotkey (default ⇧⌘2) opens the overlay within ~300 ms (see `overlay visible ... ms after trigger` in the log)
   on every display. Change hotkey… records a new combination (e.g. ⇧⌘S), refuses taken ones, survives a relaunch.
4. An open menu or tooltip is still visible in the frozen image.
5. Drag-select, click-to-select a window (rectangle matches the window), ⌘A, resize handles, arrow-key nudge.
6. Every tool draws where the cursor is (watch for y-flips or Retina 2x offsets); scroll resizes live; hover
   preview matches the stamp size.
7. Redact: brush and box; black/pixelate/blur; secure vs insecure (orange shield); Option+scroll cell size.
8. Text: type, move by dragging its box, Return; emoji pick, size, rotate by dragging.
9. Remapped App Shortcuts for Undo/Redo/Copy (System Settings > Keyboard > Keyboard Shortcuts > App Shortcuts) work
   in the overlay. ⌘C pastes into another app at full Retina resolution; ⌘S saves PNG/JPEG and reopens in the same folder next time.
10. Esc first deselects the active tool, then closes (with annotations: a hint, then a second Esc within 1.5 s closes); a second capture works; memory settles back after closing (Activity Monitor).
11. Start at login toggles and survives a log out/in; `./uninstall.sh` leaves nothing behind.

## Things most likely to need fixing

- Text and emoji drawing in the flipped contexts (`withFlippedNSContext`), and the save panel / alerts appearing
  above the `.screenSaver`-level overlay.
- Window rectangles from `CGWindowListCopyWindowInfo` on secondary displays (global to per-display conversion).
- Trackpad scrolling sensitivity (`scrollWheel` accumulates deltas).
- Keyboard focus when the overlay opens from the hotkey (it's an accessory app; see `OverlaySession.show`).
