<p align="center">
  <img src="docs/logo.png" width="96" height="96" alt="Snip logo">
</p>

<h1 align="center">Snip</h1>

<p align="center">
  <b>Screenshot and annotate on Windows. Offline, instant, tiny.</b><br>
  Win+Shift+S or PrtSc &middot; no network access &middot; ~0.5 MB &middot; 0% CPU when idle &middot; Windows 10 and 11
</p>

A trustworthy replacement for Lightshot. It runs in the tray, never touches the network, and the whole thing is
about 3,000 lines of C# you can read.

![Snip overlay with a selection, annotations and the toolbars](docs/screenshot.png)

## Using it

Press **Win+Shift+S** or **PrtSc** (or left-click the tray icon). The screen freezes and dims instantly, so open
menus, tooltips and hover states stay in the picture.

- **Drag** to select a region, or **click** a window to select exactly that window. **Ctrl+A** selects the monitor under the cursor.
- Drag the handles to resize, drag inside to move, arrow keys nudge (Shift+arrows resize).
- Pick a tool from the toolbar and draw inside the selection. Hold **Shift** for 45° lines/arrows and square boxes/circles.
- **Scroll wheel** changes the size, also while you're still holding the mouse button. A hover preview shows the
  brush, stroke or stamp at its real size before you click. The color button opens the palette. Color and size are
  remembered.
- **Ctrl+C** / **Enter**: copy to clipboard and close. **Ctrl+S**: save as (PNG/JPG/BMP), the dialog opens in the last folder you saved to.
- **Esc** or right-click: cancel. **Ctrl+Z / Ctrl+Y**: undo / redo.
- Tray icon: left-click captures; the menu has capture, use PrintScreen key (on/off), start with Windows (on/off),
  open settings folder and exit.

| Tool | Key | | Tool | Key |
|---|---|---|---|---|
| Pen | P | | Highlighter | H |
| Line | L | | Text | T |
| Arrow | A | | Numbered step (1, 2, 3…) | N |
| Rectangle | R | | Emoji stamp | M |
| Ellipse | E | | Redact | B |

Text: click to place, type, **Enter** to finish, **Shift+Enter** for a new line, **Ctrl+V** pastes text. While you're
still typing, drag the text by its dashed box to move it (right-click while dragging puts it back).

Numbered steps and emoji are placed when you release the mouse button: scroll to resize while holding. Emoji come in
full colour from Windows' own Segoe UI Emoji font; pick one from the grid next to the tool, and drag while holding the
button to rotate it (Shift snaps to 15°).

### Redact

![Redact tool with brush, box, pixelate, blur and black](docs/redact.png)

The redact bar appears next to the tool: **Brush · Box | Black · Pixelate · Blur | secure toggle | cell size**.

- **Brush** (default) paints redaction along the stroke; scroll sets the width. **Box** covers a rectangle. **F** switches.
- **Black** is a solid fill. **Pixelate** and **Blur** look like the familiar effects. **Ctrl+scroll** sets the cell
  size (4–48 px), live, even mid-stroke.
- **Secure** (shield, the default): Pixelate and Blur are generated from fresh randomness plus only a 4-tone palette of
  the covered area and the colours just outside its edge. They never use where anything was, so nothing underneath can
  be recovered, not even by averaging many screenshots of the same content.
- **Insecure** (orange shield): a real pixelation or blur of the covered pixels. It looks the most faithful, but like
  any real blur or pixelation it can be partly reversed. Black is always secure.

Every redaction is flattened into the copied or saved image; there is no hidden layer to remove.

## Install

Windows 10 or 11 (x64). Installing from source needs the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0),
which includes the Desktop Runtime that Snip runs on.

```powershell
powershell -ExecutionPolicy Bypass -File tools\install.ps1
```

This builds `Snip.exe` (~0.5 MB), copies it to `%LOCALAPPDATA%\Programs\Snip`, adds **Snip** to the Start menu and
to **Settings > Apps > Installed apps**, and starts it. Everything is per-user; no admin rights needed. The installed
copy registers itself to start with Windows (`HKCU\...\Run`); copies run from anywhere else never touch autostart.

**Starting Snip again.** If you exit Snip from the tray or turn off *Start with Windows*, start it from the Start menu
(search "Snip"). Turn autostart back on from the tray menu's *Start with Windows*. When Snip is already running,
launching it again from Start takes a screenshot.

**Uninstall** from Settings > Apps > Installed apps > Snip > Uninstall, or run `tools\uninstall.ps1`. Both remove the
app, its Start menu entry, autostart, the Installed apps entry and its settings.

## Privacy and footprint

- No network code at all. Images only go to the clipboard or to a file you pick.
- Settings live in `%APPDATA%\Snip\settings.json` (last save folder, color, size, tool options, toggles). Nothing
  else is stored.
- Idle: 0% CPU and no timers or polling. About 10 MB of private memory after start, up to about 40 MB once
  captures have loaded fonts, emoji and code; Windows reclaims what it needs. The keyboard hook runs on its own thread and
  only checks a few key states per keystroke, so typing elsewhere is never delayed. Capture buffers are released as
  soon as the overlay closes. The working set is not force-trimmed, so the hook never has to page back in mid-keystroke
  (Windows drops hooks that respond too slowly); Snip also re-arms the hook after unlock, resume and every capture.
- System DLLs are only loaded from System32, so a planted DLL next to `Snip.exe` is ignored.
- Win+Shift+S is taken over with a low-level keyboard hook (the shell owns that combo, so a normal hotkey can't
  claim it). The key is swallowed and the Start menu is suppressed; all other keys pass through untouched.

## Known limits

- While an app running **as administrator** has focus, Windows doesn't deliver its keystrokes to Snip (UIPI), so the
  hotkey falls through to Windows' own snipping tool. Run Snip elevated if you need that.
- DRM-protected video shows up black, as with every screenshot tool.
- Some apps close their open menu as soon as Win is pressed. If a menu disappears with Win+Shift+S, use PrtSc.

## Development

```powershell
dotnet test                                   # unit tests (rendering, redaction, emoji, hotkeys, clipboard, files)
powershell -STA -File tests\e2e\E2E.ps1 -Exe <Snip.exe> -OutDir <dir>        # drives mouse + keyboard
powershell -STA -File tests\e2e\Scenarios.ps1 -Exe <Snip.exe> -OutDir <dir>  # menus, window click, save dialog
powershell -File tests\e2e\Timing.ps1 -Exe <Snip.exe>                         # overlay latency, no input needed
```

Set `SNIP_TRACE=1` to log capture timings to `%APPDATA%\Snip\snip.log`. Errors are always logged there (never key data).
`dotnet run --project tools/DemoShot` regenerates `docs/screenshot.png` and `docs/redact.png` from a synthetic
backdrop (it never reads the real screen).

The icon comes from `tools/IconGen` (design `scissors-sideways-purple`):

```powershell
dotnet run --project tools/IconGen -- ico scissors-sideways-purple src/Snip/snip.ico
dotnet run --project tools/IconGen -- png scissors-sideways-purple 256 docs/logo.png
dotnet run --project tools/IconGen -- preview-scissors sheet.png      # compare shapes and colours
```

## Contributing

Read [AGENTS.md](AGENTS.md) (principles, hard rules and the PR review checklist) and
[docs/architecture.md](docs/architecture.md) (how capture, hotkeys, rendering and redaction work) first. Product
changes are discussed before they're built.

## License

[MIT](LICENSE). Snip bundles no third-party code or assets; emoji are drawn at runtime from the Segoe UI Emoji font
that ships with Windows.
