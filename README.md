# Snip

A small, private screenshot + annotation tool for Windows, built as a trustworthy replacement for Lightshot.
It runs in the tray, never touches the network, and the whole thing is a few hundred lines of C# you can read.

## Using it

Press **Win+Shift+S** or **PrtSc** (or left-click the tray icon). The screen freezes and dims instantly, so open
menus, tooltips and hover states stay in the picture.

- **Drag** to select a region, or **click** a window to select exactly that window. **Ctrl+A** selects the monitor under the cursor.
- Drag the handles to resize, drag inside to move, arrow keys nudge (Shift+arrows resize).
- Pick a tool from the toolbar and draw inside the selection. Hold **Shift** for 45° lines/arrows and square boxes/circles.
- **Scroll wheel** changes the size; the color button opens the palette. Color and size are remembered.
- **Ctrl+C** / **Enter**: copy to clipboard and close. **Ctrl+S**: save as (PNG/JPG/BMP), the dialog opens in the last folder you saved to.
- **Esc** or right-click: cancel. **Ctrl+Z / Ctrl+Y**: undo / redo.

| Tool | Key | | Tool | Key |
|---|---|---|---|---|
| Pen | P | | Highlighter | H |
| Line | L | | Text | T |
| Arrow | A | | Numbered step (1, 2, 3…) | N |
| Rectangle | R | | Redact (solid black box) | B |
| Ellipse | E | | | |

Text: click to place, type, **Enter** to finish, **Shift+Enter** for a new line, **Ctrl+V** pastes text.

Tray menu: capture, use PrintScreen key (on/off), start with Windows (on/off), open settings folder, exit.

## Install

Needs the [.NET 8 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/8.0) (x64) and, to build, the .NET 8 SDK.

```powershell
powershell -ExecutionPolicy Bypass -File tools\install.ps1
```

This builds `Snip.exe` (~400 KB), copies it to `%LOCALAPPDATA%\Programs\Snip` and starts it. On launch it registers
itself under `HKCU\...\Run` (your user only, no admin). `tools\uninstall.ps1` removes everything.

## Privacy and footprint

- No network code at all. Images only go to the clipboard or to a file you pick.
- Settings live in `%APPDATA%\Snip\settings.json` (last save folder, color, size, toggles). Nothing else is stored.
- Idle: 0% CPU, about 10 MB working set, no timers or polling. The keyboard hook runs on its own thread and only
  checks a few key states per keystroke, so typing elsewhere is never delayed. Capture buffers are released as soon
  as the overlay closes.
- Win+Shift+S is taken over with a low-level keyboard hook (the shell owns that combo, so a normal hotkey can't
  claim it). The key is swallowed and the Start menu is suppressed; all other keys pass through untouched.

## Known limits

- While an app running **as administrator** has focus, Windows doesn't deliver its keystrokes to Snip (UIPI), so the
  hotkey falls through to Windows' own snipping tool. Run Snip elevated if you need that.
- DRM-protected video shows up black, as with every screenshot tool.
- PrtSc is the most reliable key for capturing open menus: pressing Win can close some menus before Snip sees the S.

## Development

```powershell
dotnet test                                   # unit tests (rendering, geometry, clipboard, file formats)
powershell -STA -File tests\e2e\E2E.ps1 -Exe <Snip.exe> -OutDir <dir>        # drives mouse + keyboard
powershell -STA -File tests\e2e\Scenarios.ps1 -Exe <Snip.exe> -OutDir <dir>  # menus, window click, save dialog
powershell -File tests\e2e\Timing.ps1 -Exe <Snip.exe>                         # overlay latency, no input needed
```

Set `SNIP_TRACE=1` to log capture timings to `%APPDATA%\Snip\snip.log`. Errors are always logged there (never key data).
`tools\make-icon.ps1` regenerates the icon.
