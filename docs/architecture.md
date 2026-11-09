# Snip architecture

How Snip is put together and why. Read this before changing capture, the hook, rendering or redaction.
The rules that follow from it are summarised in [AGENTS.md](../AGENTS.md).

## Process model

One per-user process, started at logon from `%LOCALAPPDATA%\Programs\Snip\Snip.exe`, living in the tray.

| Thread | Does | Must never |
|---|---|---|
| UI thread (STA, WinForms) | Tray icon, capture, overlay window, painting, clipboard, save dialog | Be needed for keystrokes elsewhere to flow |
| Hook thread | Owns the `WH_KEYBOARD_LL` hook and a bare `GetMessage` loop | Do I/O, logging, allocation-heavy work or block |
| Thread pool | Waits on the "capture" named event from a second launch | Touch UI (it posts to the UI thread) |

Single instance: a `Local\` named mutex. A second launch (`Snip.exe` or `Snip.exe --capture`) sets a `Local\`
named event that the running instance turns into a capture, then exits. The event is created before the mutex so a
second launch can always find it.

## Capture pipeline

```
key down ─► HotkeyFilter (hook thread) ─► post to UI ─► ScreenCapture (BitBlt of the whole virtual desktop)
        ─► WindowFinder (window rectangles, z-order) ─► OverlayForm shown ─► user selects + annotates
        ─► Renderer.Compose (crop + flatten annotations) ─► clipboard (Bitmap + PNG) or file
```

- **Pixels first.** The screen is grabbed before any Snip window exists, so open menus, tooltips and hover states are
  in the frozen image. Nothing may be added between the hotkey and the blit.
- **Fast path.** BitBlt into a device-compatible bitmap, then `GetDIBits` straight into the GDI+ buffer. `CAPTUREBLT`
  is deliberately not used (DWM already composes layered windows; the flag only adds a slow sync). Latency budget:
  overlay visible in roughly 200 ms on a 7680×1440 desktop (`tests/e2e/Timing.ps1` measures it).
- **One overlay over all monitors.** A borderless topmost tool window covering the virtual screen. Client coordinates
  equal capture-bitmap pixels, including negative monitor offsets. The process is Per-Monitor-V2 DPI aware and the
  overlay swallows `WM_DPICHANGED` so it never resizes itself.
- **Painting.** A pre-dimmed copy of the capture is the background; the selection shows the original; annotations
  are drawn clipped to the selection; toolbars, handles and labels on top. Invalidation is region-based (selection,
  chrome, shape bounds) so dragging stays smooth on multi-4K desktops.
- **Release.** When the overlay closes, bitmaps are disposed and a GC runs. The working set is not force-trimmed:
  paging the hook's code back in on the next keystroke risks Windows silently removing the hook.

## Hotkeys

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

## Annotation model

- `Annotation` subclasses (`Stroke`, `LineShape`, `BoxShape`, `TextNote`, `NumberMark`, `EmojiMark`, `RedactMark`)
  hold geometry in capture-bitmap pixels and draw themselves with GDI+. `Bounds` must cover everything a shape
  draws, at any rotation, because invalidation relies on it.
- The overlay keeps a list plus a redo stack. A shape being drawn is `_current` until mouse-up.
- Numbered steps and emoji are created on mouse-down and placed on mouse-up, so the wheel can resize them (and drag
  rotates emoji) while the button is held.
- `Renderer.Compose` crops the selection and flattens every annotation into one opaque bitmap. Nothing is layered in
  the output.

## Redaction

`RedactMark` = shape (Brush, Box) × style (Black, Pixelate, Blur) × secure flag × cell size.

- **Black** fills the shape. Always secure.
- **Secure Pixelate / Blur** (default) are generated, not filtered. Each cell's colour comes from hashed value noise
  (fresh cryptographic seed per mark) mapped onto a 4-tone palette taken from brightness quantiles of the covered
  pixels, blended near the edge toward the average colour of pixels just outside the shape. Positions of the covered
  content never influence the output: two areas with the same colour distribution in different arrangements render
  identically (tested). Averaging many captures recovers only the palette and the surroundings.
- **Contrast floor.** If the palette's tones are within 70 brightness levels, they are spread apart so a redaction
  never looks like erased or empty space. Edge fading is capped at a quarter of the shape's smaller side for the same
  reason, and is weaker on thin brush strokes.
- **Insecure Pixelate / Blur** are real block averages / box blurs of the covered pixels. They can be partly reversed;
  the UI says so (orange shield, tooltip).
- Rendering is cached per mark and redone when its geometry, cell size or preview flag changes. While dragging,
  a cheaper preview skips edge blending.

## Emoji

GDI+ draws emoji in monochrome. `EmojiFont` reads the system Segoe UI Emoji's `cmap`, `COLR` (v0 layers) and `CPAL`
tables via `GetFontData`, turns each layer's outline (`GetGlyphOutline`) into a `GraphicsPath` and fills it with its
palette colour. Results are vector paths, cached per emoji, sharp at any size and rotation. Nothing is bundled.
Windows 10 and 11 ship different emoji designs (11 uses Fluent, some single-colour); both work.

## Settings and files

- `%APPDATA%\Snip\settings.json`: last save folder, colour, size, tool options, toggles. Written via temp file + move.
  Corrupt or missing settings fall back to defaults.
- `%APPDATA%\Snip\snip.log`: lifecycle events and errors only, capped at 256 KB. Capture timings only with
  `SNIP_TRACE=1`.
- `HKCU\...\Run\Snip`: written only by the installed copy, re-applied at each start so it follows the exe.
- Nothing else is written. No temp files, no caches on disk.

## Security model

- No network code, no telemetry, no update check.
- All P/Invoke DLLs resolve from System32 only (`DefaultDllImportSearchPaths`).
- Same-user processes can trigger captures through the named event; that is inside the user's own trust boundary and
  only shows the overlay locally.
- Images leave Snip only through the clipboard or a file the user picks.
