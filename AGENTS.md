# Agent notes

Snip is a Windows tray app (C#, .NET 10, WinForms). See README.md for features and layout.

## Hard rules

- **No network code.** No HTTP, sockets or dependencies that phone home. "Never touches the network" is a core promise.
- **Never log or store key data.** The keyboard hook sees every keystroke system-wide. `Log` may record lifecycle events
  and errors only; no temporary debug logging of keys either.
- **Keep the hook callback cheap** (`KeyboardHook.HookProc`): no I/O, allocation or blocking. It runs on every keystroke.
- **Ask the user before running `tests/e2e/E2E.ps1` or `Scenarios.ps1`.** They drive the real mouse and keyboard;
  the user must keep hands off. `dotnet test` and `tests/e2e/Timing.ps1` need no input and are always fine.
- **Don't edit sources with Windows PowerShell `Get-Content`/`Set-Content`.** PowerShell 5.1 reads UTF-8 without a
  BOM as Windows-1252 and garbles non-ASCII text (a `SourceEncodingTests` test guards this).
- **README/doc images come from `tools/DemoShot`** (synthetic backdrop), never from a real screen capture.

## Cleaning up after test runs

- A Release build re-registers `HKCU\Software\Microsoft\Windows\CurrentVersion\Run\Snip` to wherever it ran from.
  Afterwards run `tools\install.ps1` so it points at `%LOCALAPPDATA%\Programs\Snip\Snip.exe` again.
- `Scenarios.ps1` changes `LastSaveDirectory` in `%APPDATA%\Snip\settings.json`; reset it to `null`. Delete
  `snip.log` if tracing (`SNIP_TRACE=1`) was on.

## Build

- `global.json` pins the .NET 10 SDK. `nuget.config` supplies nuget.org because this machine has no global source.
- Release publish is framework-dependent single-file (~400 KB): `dotnet publish src/Snip -c Release -o artifacts/publish`.
