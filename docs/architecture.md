# Snip architecture

How Snip is put together and why. Read this before changing capture, hotkeys, rendering or redaction on either
platform. The rules that follow from it are summarised in [AGENTS.md](../AGENTS.md); macOS specifics for day-to-day
work are in [mac/AGENTS.md](../mac/AGENTS.md).

Both apps share one design: a resident process that grabs the screen the instant the hotkey fires, shows the frozen
image in a full-screen overlay, keeps annotations as vector shapes, and flattens them into one opaque image only when
copying or saving. They differ only where the operating systems do.

| | Windows (`src/Snip`, C#) | macOS (`mac/`, Swift) |
|---|---|---|
| Resident UI | Tray icon (`TrayApp`) | Menu-bar item (`AppDelegate`) |
| Hotkey | Low-level keyboard hook, decisions in `HotkeyFilter` | Carbon `RegisterEventHotKey` (`HotKey`, `HotkeySpec`) |
| Capture | GDI BitBlt of the whole virtual desktop (`ScreenCapture`) | ScreenCaptureKit, one image per display (`Capture`) |
| Overlay | One window over all monitors (`OverlayForm`) | One window per display (`OverlaySession`, `OverlayView`) |
| Geometry units | Capture-bitmap pixels | Display points (images in pixels, `scale` = pixels per point) |
| Drawing | GDI+ | Core Graphics / AppKit |
| Shared logic | `HotkeyFilter`, `Renderer`, `RedactRender` | `SnipCore` (geometry, redaction, hotkey specs) |
| Settings | `%APPDATA%\Snip\settings.json` | `defaults` domain `com.buildthomas.snip` |
| Log | `%APPDATA%\Snip\snip.log` | `~/Library/Logs/Snip/snip.log` |

## Process model

### Windows

One per-user process, started at logon from `%LOCALAPPDATA%\Programs\Snip\Snip.exe`, living in the tray.

| Thread | Does | Must never |
|---|---|---|
| UI thread (STA, WinForms) | Tray icon, capture, overlay window, painting, clipboard, save dialog | Be needed for keystrokes elsewhere to flow |
| Hook thread | Owns the `WH_KEYBOARD_LL` hook and a bare `GetMessage` loop | Do I/O, logging, allocation-heavy work or block |
| Thread pool | Waits on the "capture" named event from a second launch | Touch UI (it posts to the UI thread) |

Single instance: a `Local\` named mutex. A second launch (`Snip.exe` or `Snip.exe --capture`) sets a `Local\`
named event that the running instance turns into a capture, then exits. The event is created before the mutex so a
second launch can always find it.

### macOS

One per-user accessory app (no Dock icon) at `~/Applications/Snip.app`, registered as a login item with
`SMAppService`. Everything runs on the main thread; capture is an `async` call into ScreenCaptureKit. Single
instance: a second launch posts a distributed notification that the running copy turns into a capture, then exits.
`--set-hotkey` works the same way to make the running copy re-register its hotkey.

## Capture pipeline

```
Windows: key down ─► HotkeyFilter (hook thread) ─► post to UI ─► ScreenCapture (BitBlt of the virtual desktop)
         ─► WindowFinder ─► OverlayForm ─► select + annotate ─► Renderer.Compose ─► clipboard or file

macOS:   hotkey event ─► Capture.captureAll (SCScreenshotManager per display) ─► Capture.windowRects
         ─► OverlaySession (one OverlayView per display) ─► select + annotate ─► OverlayView.compose ─► clipboard or file
```

- **Pixels first (both).** The screen is grabbed before any Snip window exists, so open menus, tooltips and hover
  states are in the frozen image. Nothing may be added between the hotkey and the grab.
- **Windows fast path.** BitBlt into a device-compatible bitmap, then `GetDIBits` straight into the GDI+ buffer.
  `CAPTUREBLT` is deliberately not used (DWM already composes layered windows; the flag only adds a slow sync).
  Latency: overlay visible in roughly 200 ms on a 7680×1440 desktop (`tests/e2e/Timing.ps1` measures it).
- **Windows overlay.** A borderless topmost tool window covering the virtual screen; client coordinates equal
  capture-bitmap pixels, including negative monitor offsets. The process is Per-Monitor-V2 DPI aware and the overlay
  swallows `WM_DPICHANGED` so it never resizes itself. A pre-dimmed copy of the capture is the background, and
  invalidation is region-based so dragging stays smooth on multi-4K desktops.
- **macOS overlay.** A borderless window per display at `.screenSaver` level (above full-screen apps and the menu bar);
  each view is flipped (top-left origin) and works in that display's points. A selection lives on one display. The
  view currently repaints fully on each change.
- **Release (both).** When the overlay closes, images and cached tiles are released. On Windows a GC runs, but the
  working set is not force-trimmed: paging the hook's code back in on the next keystroke risks Windows silently
  removing the hook.

## Hotkeys

### Windows

- Win+Shift+S belongs to the shell, so `RegisterHotKey` can't claim it. A low-level keyboard hook swallows the S
  key-down/up only; Win and Shift pass through. A tagged dummy key (VK 0xE8, `dwExtraInfo` = "SNIP") is injected so the
  Start menu doesn't open when Win is released.
- PrtSc without modifiers is taken too (toggle in the tray). Alt+PrtSc, Win+PrtSc and Ctrl+PrtSc stay with Windows.
- All decisions live in `HotkeyFilter`, a pure state machine fed (vk, down/up, timestamp, modifiers). It handles key
  repeat, keyboards that only report PrtSc on release, lost key-ups (stale after 1.5 s) and toggling PrtSc mid-press.
  `KeyboardHook` only reads key state and forwards; keep it that way so the logic stays unit tested.
- Windows drops a hook that ever exceeds `LowLevelHooksTimeout`, without telling anyone. Snip re-registers the hook
  after unlock, resume and every capture.
- Known OS limit: while an elevated window has focus, UIPI keeps its keystrokes from a non-elevated hook.

### macOS

- Carbon `RegisterEventHotKey`: it only ever sees its own combination and needs no Accessibility or Input Monitoring
  permission. Any combination with ⌘, ⌥ or ⌃ can be set (Change hotkey… recorder or `--set-hotkey`); `HotkeySpec`
  parses, prints and validates them. Registration fails if another app or a system shortcut owns the combination, in
  which case the previous hotkey stays.
- Overlay commands (Undo, Redo, Copy, Paste, Select All, Save…) are items in a hidden main menu rather than
  hard-coded keys, because macOS applies the user's App Shortcuts by matching menu item titles.

## Annotation model (both)

- Shapes (stroke, line/arrow, box/ellipse, text, numbered step, emoji, redaction) hold their geometry and draw
  themselves. `Bounds` must cover everything a shape draws, at any rotation, because invalidation relies on it.
- The overlay keeps a list plus a redo stack. A shape being drawn stays "current" until mouse-up.
- Numbered steps and emoji are created on mouse-down and placed on mouse-up, so the wheel can resize them (and drag
  rotates emoji) while the button is held. Text being typed can be dragged by its box until it's committed.
- Sizes come from one shared 1-20 setting through identical mappings on both apps (brush `8 + 4*size`, emoji
  `16 + 6*size`, text `8 + 3*size`, number radius `10 + 2*size`).
- Composing crops the selection and flattens every annotation into one opaque image. Nothing is layered in the output.

## Redaction (both)

A redaction = shape (Brush, Box) × style (Black, Pixelate, Blur) × secure flag × cell size. The algorithm is
implemented twice with matching tests: `RedactRender` (Windows) and `SnipCore/Redaction.swift` (macOS).

- **Black** fills the shape. Always secure.
- **Secure Pixelate / Blur** (default) are generated, not filtered. Each cell's colour comes from hashed value noise
  (fresh random seed per mark) mapped onto a 4-tone palette taken from brightness quantiles of the covered pixels,
  blended near the edge toward the average colour of pixels just outside the shape. Positions of the covered content
  never influence the output: two areas with the same colour distribution in different arrangements render
  identically (tested on both). Averaging many captures recovers only the palette and the surroundings.
- **Contrast floor.** If the palette's tones are within 70 brightness levels, they are spread apart so a redaction
  never looks like erased or empty space. Edge fading is capped at a quarter of the shape's smaller side for the same
  reason, and is weaker on thin brush strokes.
- **Insecure Pixelate / Blur** are real block averages / box blurs of the covered pixels. They can be partly reversed;
  the UI says so (orange shield, tooltip).
- Rendering is cached per mark and redone when its geometry, cell size or preview flag changes. While dragging, a
  cheaper preview skips edge blending.

## Emoji

- **Windows:** GDI+ draws emoji in monochrome, so `EmojiFont` reads the system Segoe UI Emoji's `cmap`, `COLR` (v0
  layers) and `CPAL` tables via `GetFontData`, turns each layer's outline (`GetGlyphOutline`) into a `GraphicsPath` and
  fills it with its palette colour. Vector paths, cached per emoji, sharp at any size and rotation. Windows 10 and 11
  ship different designs (11 uses Fluent, some single-colour); both work.
- **macOS:** Apple Color Emoji drawn as text through AppKit, rotated with the context transform.
- Nothing is bundled on either platform.

## Settings and files

Windows:

- `%APPDATA%\Snip\settings.json`: last save folder, colour, size, tool options, toggles. Written via temp file + move.
  Corrupt or missing settings fall back to defaults.
- `%APPDATA%\Snip\snip.log`: lifecycle events and errors only, capped at 256 KB. Capture timings only with
  `SNIP_TRACE=1`.
- `HKCU\...\Run\Snip`: written only by the installed copy, re-applied at each start so it follows the exe.
- Written by `tools/install.ps1`, removed by `uninstall.ps1`: `%LOCALAPPDATA%\Programs\Snip` (`Snip.exe` and a copy
  of `uninstall.ps1`), the Start menu shortcut, and `HKCU\...\Uninstall\Snip`, which lists Snip under Settings >
  Apps > Installed apps with the uninstall script as its uninstall command.

macOS:

- `defaults` domain `com.buildthomas.snip`: the same settings plus the hotkey.
- `~/Library/Logs/Snip/snip.log` (rotates at 1 MB): lifecycle, timings and errors; debug detail while the port is
  experimental. Also mirrored to the unified log (subsystem `com.buildthomas.snip`).
- Login item via `SMAppService`, registered only by an installed copy in `~/Applications` or `/Applications`.
- Written by `mac/install.sh`, removed by `mac/uninstall.sh`: `~/Applications/Snip.app`; the uninstaller also removes
  the login item, settings, logs and the Screen Recording permission entry.

Nothing else is written on either platform. No temp files, no caches on disk.

## Security model

- No network code, no telemetry, no update check, on either platform.
- Windows: all P/Invoke DLLs resolve from System32 only (`DefaultDllImportSearchPaths`). Same-user processes can
  trigger captures through the named event; that is inside the user's own trust boundary and only shows the overlay
  locally.
- macOS: capture needs the user's Screen Recording permission; the hotkey needs no input-monitoring permission. Same-user
  processes can trigger captures through the distributed notification, with the same reasoning as on Windows.
- Images leave Snip only through the clipboard or a file the user picks.
