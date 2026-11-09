# Agent notes

Snip is a Windows 10/11 tray app (C#, .NET 10, WinForms): a small, offline, trustworthy screenshot + annotation tool.
README.md describes the features; [docs/architecture.md](docs/architecture.md) explains how it works and why.
This file is the rulebook for agents writing or reviewing changes.

## Product principles

- **The owner makes the product decisions.** New features, changed behaviour, new defaults, shortcuts or UI layout:
  propose them (with options and a recommendation) and wait for a yes. Bug fixes and internal cleanups don't need that.
- **Trustworthy by construction.** Small, readable code; no network; no dependencies at runtime; nothing stored beyond
  `settings.json`. A user should be able to audit the whole app.
- **No runtime cost for the rest of the system.** 0% CPU when idle, no timers or polling outside the open overlay,
  keystrokes elsewhere never delayed.
- **Instant.** Pixels are captured before any Snip window appears; the overlay shows in about 200 ms. Never add work
  between the hotkey and the screen grab.
- **Redaction must be honest.** What is redacted can't be recovered from the output, and anything that could be
  partly reversed is labelled as such in the UI.
- **Plain, quiet UI.** Plain punctuation in UI text (a hyphen, not an em dash or middle dot); short tooltips with the
  shortcut in brackets; remembered settings instead of prompts.

## Hard rules

- **No network code.** No HTTP, sockets, update checks, telemetry, or packages that phone home.
- **No new runtime dependencies** (NuGet packages, bundled DLLs or assets) without the owner's approval. Test-only
  packages are fine. Use what Windows and .NET already provide.
- **Never log or store key data.** The keyboard hook sees every keystroke system-wide. `Log` records lifecycle events
  and errors only; no temporary debug logging of keys either.
- **Keep the hook callback cheap** (`KeyboardHook.HookProc`): no I/O, logging, locks or blocking. Hotkey decisions go
  in `HotkeyFilter` (pure, unit tested); `KeyboardHook` only feeds it.
- **Don't trim the working set** or otherwise page out the hook; Windows silently drops slow hooks.
- **Secure redaction stays content-independent.** Secure Pixelate/Blur may use only the covered area's brightness
  palette (quantiles), the colours just outside the shape and randomness, never where things were.
  `SecureStyles_DependOnlyOnColourDistribution_NotOnWhereThingsAre` must keep passing. Black is always secure.
  Redactions must stay visibly redactions (contrast floor) and be flattened into the output.
- **Only the installed copy touches autostart** (`HKCU\...\Run`), per-user, never admin. Installs stay per-user too.
- **P/Invoke DLLs load from System32 only** (`DefaultDllImportSearchPaths` in `Native.cs`). Keep it.
- **Errors are shown, never swallowed.** Copy, save and capture failures tell the user; `Application.ThreadException`
  only logs as a last resort.
- **Ask the user before running `tests/e2e/E2E.ps1` or `Scenarios.ps1`.** They drive the real mouse and keyboard and
  the user must keep hands off. `dotnet test` and `tests/e2e/Timing.ps1` need no input and are always fine.
- **README/doc images come from `tools/DemoShot`** (synthetic backdrop), never from a real screen capture.
- **Keep docs, comments and commit messages about the tool itself**, described generically, not about any particular
  person's purpose for using it.
- **Don't edit sources with Windows PowerShell `Get-Content`/`Set-Content`.** PowerShell 5.1 reads UTF-8 without a
  BOM as Windows-1252 and garbles non-ASCII text (`SourceEncodingTests` guards this).

## Conventions

- Comments: none by default; one line when the why isn't obvious; never a paragraph.
- Pure logic in testable classes (`HotkeyFilter`, `Renderer`, `RedactRender`, `EmojiFont` parsing); WinForms and
  P/Invoke stay thin.
- Annotation geometry is in capture-bitmap pixels; `Bounds` must cover everything a shape draws at any rotation.
- Sizes follow the shared size setting (1-20) through one mapping per tool (`RedactMark.BrushWidthFor`,
  `EmojiMark.PixelsFor`, `TextNote.FontPx`, ...). Scroll changes size live, also mid-drag, except a redaction's shape
  (Ctrl+scroll still changes its cell size).
- New tool options go in a bar or grid beside the tool button (like the redact bar and emoji picker), are remembered
  in `Settings`, and get a hover preview when size matters.
- Settings: add a property with a safe default; validate on load (`Enum.IsDefined`, clamping); enum values are stored,
  so never renumber them.
- Release resources: anything holding bitmaps implements `IDisposable` and is disposed with the overlay.
- Support Windows 10 and 11. Fonts differ between them (emoji designs especially); don't assert on exact glyph looks.

## Testing

- `dotnet test` must pass. New behaviour comes with tests, especially hotkey sequences, rendering and redaction
  properties.
- Performance-sensitive changes (capture, overlay, painting): run `tests/e2e/Timing.ps1` before and after.
- UI or hotkey changes: ask the user for an E2E round (see Hard rules).

### Cleaning up after E2E runs

- E2E runs kill the installed Snip; run `tools\install.ps1` afterwards to start it again. Test builds never touch the
  `Run` entry.
- `Scenarios.ps1` changes `LastSaveDirectory` in `%APPDATA%\Snip\settings.json`; note the value first and restore it
  afterwards. Delete `snip.log` if tracing (`SNIP_TRACE=1`) was on.

## Build

- `global.json` pins the .NET 10 SDK. `nuget.config` supplies nuget.org in case the machine has no NuGet source set up.
- Release publish is framework-dependent single-file (~0.5 MB): `dotnet publish src/Snip -c Release -o artifacts/publish`.
- Shortcuts created from an agent's tool environment can be ignored by Start (it never lists them), while the same
  `install.ps1` run from the user's own terminal works. When checking Start menu or install behaviour, have the
  user run the installer themselves before concluding anything is broken.
- `tools/install.ps1` builds, installs to `%LOCALAPPDATA%\Programs\Snip` (with a copy of `uninstall.ps1`), adds the
  Start menu shortcut and the per-user `HKCU\...\Uninstall\Snip` entry, and starts it. `tools/uninstall.ps1` is what
  Settings > Installed apps runs; it must keep removing everything the installer adds, and stay ASCII (Windows
  PowerShell 5.1 reads it as ANSI).

## PR review checklist

- [ ] Product change? Approved by the owner (linked in the PR), or it's a pure fix/cleanup.
- [ ] No network, telemetry or new runtime dependency.
- [ ] Hook path unchanged, or still I/O-free with logic in `HotkeyFilter` plus tests.
- [ ] Nothing new runs while idle (timers, polling, background threads).
- [ ] Nothing added before the screen grab; Timing numbers included if capture or paint changed.
- [ ] Nothing new written to disk or the registry; nothing that could contain keystrokes or screen content in logs.
- [ ] Redaction: secure styles still content-independent (test passes), output flattened, insecure clearly labelled.
- [ ] Failures reach the user; a `catch` that only logs is for cases the user can't act on (e.g. settings I/O).
- [ ] Bitmaps, fonts, pens and timers disposed; overlay leaves nothing behind.
- [ ] Settings changes backward compatible (defaults, validation, stable enum values).
- [ ] UI text uses plain punctuation and puts the shortcut in the tooltip; README and screenshots updated if the UI changed.
- [ ] Docs and commit message describe the change generically.
- [ ] `dotnet test` passes; E2E run (with the user's OK) for UI or hotkey changes.
