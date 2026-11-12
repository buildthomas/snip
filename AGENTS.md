# Agent notes

Snip is a small, offline, trustworthy screenshot + annotation tool with two native apps:

| | Windows 10/11 | macOS 14+ |
|---|---|---|
| Code | `src/Snip` (C#, .NET 10, WinForms), tests in `tests/`, tools in `tools/` | `mac/` (Swift, AppKit, ScreenCaptureKit), pure logic in `mac/Sources/SnipCore` |
| Lives in | Tray | Menu bar |
| Platform notes | this file, [docs/architecture.md](docs/architecture.md) | [mac/AGENTS.md](mac/AGENTS.md), [mac/README.md](mac/README.md) |

README.md describes the features for both. This file is the rulebook for agents writing or reviewing changes on
either platform.

## Product principles

- **The owner makes the product decisions.** New features, changed behaviour, new defaults, shortcuts or UI layout:
  propose them (with options and a recommendation) and wait for a yes. Bug fixes and internal cleanups don't need that.
- **One product, two apps.** A user-facing feature, behaviour change or default lands in **both** the Windows and the
  macOS app, in the same PR where possible (see [Feature parity](#feature-parity)).
- **Trustworthy by construction.** Small, readable code; no network; no dependencies at runtime; nothing stored beyond
  the settings, apart from recordings waiting in the temp folder until they're saved or discarded. A user should be
  able to audit the whole app.
- **No runtime cost for the rest of the system.** 0% CPU when idle, no timers or polling outside the open overlay,
  keystrokes elsewhere never delayed.
- **Instant.** Pixels are captured before any Snip window appears; the overlay shows in about 200-300 ms. Never add
  work between the hotkey and the screen grab.
- **Redaction must be honest.** What is redacted can't be recovered from the output, and anything that could be
  partly reversed is labelled as such in the UI.
- **Plain, quiet UI.** Plain punctuation in UI text (a hyphen, not an em dash or middle dot); short tooltips with the
  shortcut in brackets; remembered settings instead of prompts. Each app uses its platform's conventions for modifier
  keys (Ctrl on Windows, ⌘ on macOS) and menus.

## Feature parity

- **Add features to both apps.** Implement a new feature on Windows and macOS, with matching behaviour, defaults, tool
  keys, size mappings and tests. Shared algorithms (secure redaction, geometry) must stay equivalent: change them in
  `src/Snip` and `mac/Sources/SnipCore` together and port the tests.
- **If one side can't be done in the same PR** (no machine or toolchain for it, platform limitation), say so in the PR,
  add the gap to the table below, and don't let the docs claim the feature on the platform that lacks it.
- **Platform-specific is fine where the platforms differ** (PrintScreen key, App Shortcuts, permissions), as long as
  the table says so.
- **Docs cover both.** README instructions and shortcuts are given for Windows and macOS; never describe Snip as a
  single-platform tool.

| Feature | Windows | macOS | Notes |
|---|---|---|---|
| Region / window / whole-display selection | ✅ | ✅ | |
| Selection spanning two monitors | ✅ | ❌ | macOS selection lives on one display |
| All tools, hover preview, scroll-to-resize, move text while typing | ✅ | ✅ | |
| Redaction (brush/box, black/pixelate/blur, secure toggle, cell size) | ✅ Ctrl+scroll | ✅ Option+scroll | |
| Emoji stamps with rotation | ✅ | ✅ | system emoji font on each |
| Copy, save as (remembers folder) | ✅ PNG/JPG/BMP | ✅ PNG/JPEG | |
| Capture hotkey | Win+Shift+S and PrtSc (fixed) | any combination (Change hotkey…, `--set-hotkey`) | **gap:** no custom hotkey on Windows yet |
| PrintScreen toggle | ✅ | n/a | Windows-only key |
| User-remapped shortcuts for Undo/Redo/Copy/Paste/Select All/Save | n/a | ✅ via menu items | macOS App Shortcuts |
| Start with Windows / at login | ✅ | ✅ | |
| Installed-apps listing, uninstall | ✅ Start menu + Installed apps | ✅ `~/Applications`, `uninstall.sh` | |
| Screen recording to MP4 (record hotkey with live selection, Stop bar, Copy/Save/Play/Discard, system sound and mic toggles) | ✅ Win+Shift+R | ❌ | **gap:** macOS port pending (ScreenCaptureKit + AVAssetWriter) |
| Diagnostics | log + `SNIP_TRACE=1` | log + `--diagnostics` | **gap:** no `--diagnostics` on Windows |

## Hard rules

Both apps:

- **No network code.** No HTTP, sockets, update checks, telemetry, or packages that phone home.
- **No new runtime dependencies** (NuGet or Swift packages, bundled libraries or assets) without the owner's approval.
  Test-only dependencies are fine. Use what the OS and the platform SDK already provide.
- **Never log or store key data or screen content.** Logs record lifecycle events, timings and errors only; no
  temporary debug logging of keys either.
- **Secure redaction stays content-independent.** Secure Pixelate/Blur may use only the covered area's brightness
  palette (quantiles), the colours just outside the shape and randomness, never where things were. These tests must
  keep passing: `SecureStyles_DependOnlyOnColourDistribution_NotOnWhereThingsAre` (Windows) and
  `testSecureOutputDependsOnlyOnColourDistributionNotPositions` (macOS). Black is always secure. Redactions must stay
  visibly redactions (contrast floor) and be flattened into the output.
- **Only the installed copy touches autostart**, per-user, never admin. Installs stay per-user too.
- **Errors are shown, never swallowed.** Copy, save and capture failures tell the user.
- **README/doc images come from `tools/DemoShot`** (synthetic backdrop), never from a real screen capture.
- **Keep docs, comments and commit messages about the tool itself**, described generically, not about any particular
  person's purpose for using it.
- **Agent tool environments can be virtualized.** An agent's own processes may see a private copy of the user's
  profile, settings and registry (Windows `%APPDATA%`/`%LOCALAPPDATA%`/`HKCU`; possibly `~/Library` and `defaults` on
  macOS). Run installs, uninstalls, settings changes and every check of them in the user's own shell (e.g. the
  Terminal panel) before concluding anything is broken.

Windows:

- **Keep the hook callback cheap** (`KeyboardHook.HookProc`): no I/O, logging, locks or blocking. Hotkey decisions go
  in `HotkeyFilter` (pure, unit tested); `KeyboardHook` only feeds it.
- **Recording stays on the GPU** where the device allows (`ScreenRecorder`: Desktop Duplication into a texture, Media
  Foundation's hardware H.264 encoder via a DXGI device manager). The CPU copy path is only a fallback. Check
  `RecordingBenchmark` (size and CPU) after changing it.
- **Don't trim the working set** or otherwise page out the hook; Windows silently drops slow hooks.
- **P/Invoke DLLs load from System32 only** (`DefaultDllImportSearchPaths` in `Native.cs`). Keep it.
- **Ask the user before running the `tests/e2e` scripts** (`E2E.ps1`, `Scenarios.ps1`, `EscGuard.ps1`, `Record.ps1`).
  They drive the real mouse and keyboard and the user must keep hands off; they also stop the installed Snip. `dotnet test` and `tests/e2e/Timing.ps1` need no input and are always fine.
- **Don't edit sources with Windows PowerShell `Get-Content`/`Set-Content`.** PowerShell 5.1 reads UTF-8 without a
  BOM as Windows-1252 and garbles non-ASCII text (`SourceEncodingTests` guards this).

macOS:

- **Hotkey via Carbon `RegisterEventHotKey` only.** No event taps or anything needing Accessibility or Input
  Monitoring permission.
- **Overlay commands are menu items** (Undo, Redo, Copy, Paste, Select All, Save…) in the hidden main menu, never
  hard-coded keys, so the user's App Shortcuts apply. Add new commands the same way, with standard titles.

## Conventions

- Comments: none by default; one line when the why isn't obvious; never a paragraph.
- Pure logic in testable code (Windows: `HotkeyFilter`, `Renderer`, `RedactRender`, `EmojiFont` parsing; macOS:
  `SnipCore`); UI and OS interop stay thin.
- Annotation geometry: capture-bitmap pixels on Windows, display points on macOS (redaction converts to pixels).
  `Bounds` must cover everything a shape draws at any rotation.
- Sizes follow the shared size setting (1-20) through one mapping per tool, identical on both apps: brush width
  `8 + 4*size`, emoji `16 + 6*size`, text `8 + 3*size`, number radius `10 + 2*size`. Scroll changes size live, also
  mid-drag, except a redaction's shape (the cell-size modifier still works mid-drag).
- New tool options go in a bar or grid beside the tool button (like the redact bar and emoji picker), are remembered
  in the settings, and get a hover preview when size matters.
- Settings: add a value with a safe default; validate on load; stored enum values are never renumbered.
- Release resources: everything holding bitmaps is released with the overlay.
- Support Windows 10 and 11, and macOS 14+ on Apple silicon and Intel. Fonts differ between OS versions (emoji designs
  especially); don't assert on exact glyph looks.

## Testing

- **Unit tests must pass on both:** `dotnet test` (Windows) and `swift test` in `mac/` (runs on CI,
  `.github/workflows/mac.yml`, if you have no Mac). New behaviour comes with tests on both sides, especially hotkey
  handling, rendering and redaction properties.
- **Performance-sensitive changes** (capture, overlay, painting): Windows `tests/e2e/Timing.ps1` before and after;
  macOS the `overlay visible ... ms after trigger` lines in `~/Library/Logs/Snip/snip.log`.
- **UI or hotkey changes:** Windows: ask the user for an E2E round (see Hard rules). macOS: run the verification
  checklist in [mac/AGENTS.md](mac/AGENTS.md) on a real Mac, or ask the user to.

### Cleaning up after Windows E2E runs

- E2E runs kill the installed Snip; run `tools\install.ps1` afterwards to start it again. Test builds never touch the
  `Run` entry.
- `Scenarios.ps1` changes `LastSaveDirectory` in `%APPDATA%\Snip\settings.json`; note the value first and restore it
  afterwards. Delete `snip.log` if tracing (`SNIP_TRACE=1`) was on.

## Build

Windows:

- `global.json` pins the .NET 10 SDK. `nuget.config` supplies nuget.org in case the machine has no NuGet source set up.
- Release publish is framework-dependent single-file (~0.5 MB): `dotnet publish src/Snip -c Release -o artifacts/publish`.
- `tools/install.ps1` builds, installs to `%LOCALAPPDATA%\Programs\Snip` (with a copy of `uninstall.ps1`), adds the
  Start menu shortcut and the per-user `HKCU\...\Uninstall\Snip` entry, and starts it. `tools/uninstall.ps1` is what
  Settings > Installed apps runs; it must keep removing everything the installer adds, and stay ASCII (Windows
  PowerShell 5.1 reads it as ANSI).

macOS:

- Swift package in `mac/` (tools version 5.9, Swift 5 language mode). `swift build`, `swift test`.
- `mac/install.sh` builds a release binary, assembles and signs `~/Applications/Snip.app`, and launches it;
  `mac/uninstall.sh` must keep removing everything the app and installer add (app, login item, `defaults`, logs, the
  Screen Recording entry).

## PR review checklist

- [ ] Product change? Approved by the owner (linked in the PR), or it's a pure fix/cleanup.
- [ ] Both apps updated, or the gap is stated in the PR and recorded in the feature parity table; docs don't claim
      what a platform lacks.
- [ ] No network, telemetry or new runtime dependency.
- [ ] Windows hook path unchanged, or still I/O-free with logic in `HotkeyFilter` plus tests; macOS hotkey still Carbon
      only, overlay commands still menu items.
- [ ] Nothing new runs while idle (timers, polling, background threads).
- [ ] Nothing added before the screen grab; timing numbers included if capture or paint changed.
- [ ] Nothing new written to disk, the registry or `defaults` (recordings only in the temp folder until saved or
      discarded); nothing that could contain keystrokes or screen content in logs.
- [ ] Redaction: secure styles still content-independent (tests pass on both), output flattened, insecure clearly
      labelled.
- [ ] Failures reach the user; a `catch` that only logs is for cases the user can't act on (e.g. settings I/O).
- [ ] Bitmaps, images and timers released; overlay leaves nothing behind.
- [ ] Settings changes backward compatible (defaults, validation, stable enum values) on both.
- [ ] UI text uses plain punctuation and puts the shortcut in the tooltip; README (both platforms) and screenshots
      updated if the UI changed.
- [ ] Docs and commit message describe the change generically.
- [ ] `dotnet test` and `swift test` (CI) pass; E2E run or the macOS checklist for UI or hotkey changes.
