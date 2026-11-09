# Snip for macOS (experimental)

A native Swift port of Snip: menu-bar app, global hotkey, instant frozen capture of every display, the same
annotation tools and the same secure redaction as the Windows version. It has only been compiled and unit-tested
on CI so far; treat it as a first attempt and report what breaks (see [AGENTS.md](AGENTS.md)).

Requirements: macOS 14 (Sonoma) or newer, Apple silicon or Intel, Xcode command-line tools (`xcode-select --install`).
No Apple Developer account or admin rights needed.

## Install from source

```bash
git clone https://github.com/buildthomas/snip && cd snip/mac
./install.sh
```

This builds a release binary, assembles `~/Applications/Snip.app`, signs it locally and launches it. A scissors icon
appears in the menu bar. The first capture asks for **Screen Recording** permission (System Settings > Privacy &
Security > Screen Recording); after granting it, quit Snip from its menu and open it again.

Optional, recommended: run `./scripts/create-signing-identity.sh` once before installing. It creates a local
signing certificate so macOS keeps the Screen Recording permission across rebuilds; without it, each rebuild is
ad-hoc signed and may need the permission granted again.

Update: `git pull && ./install.sh`. Remove everything: `./uninstall.sh`.

## Using it

- **Hotkey:** ⇧⌘2 by default. Set any combination with **Change hotkey…** in Snip's menu (press it, then Save), or
  from a terminal: `~/Applications/Snip.app/Contents/MacOS/Snip --set-hotkey cmd+shift+s`. It needs ⌘, ⌥ or ⌃;
  combinations macOS or another app already owns are refused (the previous hotkey stays). To use ⇧⌘3/4/5, turn the
  system screenshot shortcut off first (System Settings > Keyboard > Keyboard Shortcuts > Screenshots). You can also
  click **Capture** in the menu, or run `open -a Snip --args --capture` while Snip is running.
- **Select:** drag a region or click a window. ⌘A selects the whole display. Drag handles to resize, drag inside to
  move, arrow keys nudge (Shift+arrows resize).
- **Tools:** Pen (P), Line (L), Arrow (A), Rectangle (R), Ellipse (E), Highlighter (H), Text (T), Numbered step (N),
  Emoji (M), Redact (B). Scroll changes the size, also while still holding the mouse button; Shift snaps lines to 45°
  and makes boxes square. Emoji: drag while holding to rotate (Shift snaps to 15°). Text: drag its dashed box to move
  it while typing; Return finishes, Shift+Return adds a line.
- **Redact:** bar beside the tool: Brush · Box | Black · Pixelate · Blur | secure toggle | cell size. **F** switches
  shape, **Option+scroll** sets the cell size. Secure (default) pixelate/blur are generated so nothing underneath can
  be recovered; the orange shield means a real (reversible) pixelate/blur.
- **Output:** ⌘C or Return copies and closes, ⌘S saves (opens in the last folder), Esc or right-click cancels,
  ⌘Z / ⇧⌘Z undo and redo. Undo, Redo, Copy, Paste, Select All and Save… are regular menu items, so shortcuts you
  customised in System Settings > Keyboard > Keyboard Shortcuts > App Shortcuts apply to them.

## Privacy

No network code. Images only go to the clipboard or a file you pick. Settings live in `defaults` domain
`com.buildthomas.snip`; the log in `~/Library/Logs/Snip/` records lifecycle, timings and errors, never typed text or
screen content. The hotkey uses Carbon's `RegisterEventHotKey`, which sees only its own key combination and needs no
Accessibility or Input Monitoring permission.

## Known gaps

- A selection lives on one display (no selections spanning two monitors).
- The overlay repaints fully on every change; fine on typical displays, possibly heavy on very large ones.
- Not notarized; it's meant to be built from source on the machine that runs it.
