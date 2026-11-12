import AppKit
import SnipCore

final class SelectWindow: NSWindow {
    override var canBecomeKey: Bool { true }
    override var canBecomeMain: Bool { true }
}

/// Record hotkey: pick what to record over the live screen. One see-through window per display; the selection lives on
/// one of them. Snip's windows never appear in the recording (the recorder excludes this app).
final class RecordSelectSession {
    private var windows: [SelectWindow] = []
    private var views: [RecordSelectView] = []
    var onRecord: ((NSScreen, CGRect) -> Void)?
    var onClose: (() -> Void)?
    private var closed = false
    private let previous = NSWorkspace.shared.frontmostApplication

    init() {
        for screen in NSScreen.screens {
            let window = SelectWindow(contentRect: screen.frame, styleMask: .borderless, backing: .buffered, defer: false)
            window.level = .screenSaver
            window.collectionBehavior = [.canJoinAllSpaces, .fullScreenAuxiliary, .stationary, .ignoresCycle]
            window.isOpaque = false
            window.backgroundColor = .clear
            window.hasShadow = false
            window.isReleasedWhenClosed = false
            window.acceptsMouseMovedEvents = true
            window.ignoresMouseEvents = false
            window.sharingType = .none
            let view = RecordSelectView(frame: CGRect(origin: .zero, size: screen.frame.size), screen: screen,
                                        windowRects: Capture.windowRects(for: screen))
            view.session = self
            window.contentView = view
            window.setFrame(screen.frame, display: false)
            windows.append(window)
            views.append(view)
        }
    }

    func show() {
        NSApp.activate(ignoringOtherApps: true)
        for w in windows { w.orderFrontRegardless() }
        let mouse = NSEvent.mouseLocation
        let key = windows.first { $0.frame.contains(mouse) } ?? windows.first
        key?.makeKeyAndOrderFront(nil)
        key?.makeFirstResponder(key?.contentView)
        Log.debug("record selection shown on \(windows.count) display(s)")
    }

    func selectionStarted(on view: RecordSelectView) {
        for v in views where v !== view { v.clearSelection() }
        view.window?.makeKeyAndOrderFront(nil)
    }

    func record(_ view: RecordSelectView, area: CGRect) {
        let screen = view.screen
        close(reason: "record")
        onRecord?(screen, area)
    }

    func close(reason: String) {
        guard !closed else { return }
        closed = true
        Log.info("record selection closed: \(reason)")
        for w in windows { w.orderOut(nil); w.contentView = nil }
        windows.removeAll()
        views.removeAll()
        // Give focus back, so the app being recorded keeps taking keystrokes.
        if let previous, previous.processIdentifier != ProcessInfo.processInfo.processIdentifier { previous.activate(options: []) }
        onClose?()
    }
}

private enum SelectDrag { case none, pending, new, move, resize }

final class RecordSelectView: NSView {
    weak var session: RecordSelectSession?
    let screen: NSScreen
    private let windowRects: [CGRect]
    private var selection: CGRect?
    private var hover: CGRect?
    private var drag = SelectDrag.none
    private var down = CGPoint.zero
    private var start = CGRect.zero
    private var handle = -1
    private let bar = RecordSetupBar()

    init(frame: CGRect, screen: NSScreen, windowRects: [CGRect]) {
        self.screen = screen
        self.windowRects = windowRects
        super.init(frame: frame)
        wantsLayer = true
        bar.isHidden = true
        bar.onRecord = { [weak self] in self?.record() }
        bar.onCancel = { [weak self] in self?.session?.close(reason: "cancel") }
        addSubview(bar)
        addTrackingArea(NSTrackingArea(rect: .zero, options: [.mouseMoved, .activeAlways, .inVisibleRect], owner: self))
    }

    required init?(coder: NSCoder) { fatalError("not used") }

    override var isFlipped: Bool { true }
    override var acceptsFirstResponder: Bool { true }
    override func acceptsFirstMouse(for event: NSEvent?) -> Bool { true }

    private var shown: CGRect? { selection ?? hover }

    func clearSelection() {
        let old = shown
        selection = nil
        hover = nil
        bar.isHidden = true
        refresh(old)
    }

    // MARK: - Drawing

    /// Only the band around the old and new selection is redrawn, so dragging stays cheap on large displays.
    private func refresh(_ old: CGRect?) {
        let rects = [old, shown].compactMap { $0?.insetBy(dx: -120, dy: -40) }
        if rects.isEmpty || old == nil || shown == nil { needsDisplay = true; return }
        setNeedsDisplay(rects.reduce(rects[0]) { $0.union($1) })
    }

    override func draw(_ dirtyRect: NSRect) {
        let dim = NSBezierPath(rect: bounds)
        if let hole = shown { dim.appendRect(hole) }
        dim.windingRule = .evenOdd
        NSColor.black.withAlphaComponent(0.35).setFill()
        dim.fill()
        guard let hole = shown else { return }
        // Nearly invisible, but not fully transparent, so clicks inside the selection still reach this window.
        NSColor.black.withAlphaComponent(0.005).setFill()
        NSBezierPath(rect: hole).fill()
        RecordingStyle.accent.setStroke()
        let edge = NSBezierPath(rect: hole.insetBy(dx: -0.5, dy: -0.5))
        edge.lineWidth = 1
        edge.stroke()
        if selection != nil && drag == .none {
            for p in Geometry.handlePoints(hole) {
                let r = CGRect(x: p.x - 3.5, y: p.y - 3.5, width: 7, height: 7)
                NSColor.white.setFill()
                NSBezierPath(rect: r).fill()
                RecordingStyle.accent.setStroke()
                NSBezierPath(rect: r).stroke()
            }
        }
        let scale = screen.backingScaleFactor
        let px = RecordingMath.even(Int((hole.width * scale).rounded()), Int((hole.height * scale).rounded()))
        let attrs: [NSAttributedString.Key: Any] = [.font: NSFont.systemFont(ofSize: 12), .foregroundColor: NSColor.white]
        let text = "\(px.width) × \(px.height)" as NSString
        let size = text.size(withAttributes: attrs)
        var r = CGRect(x: hole.minX, y: hole.minY - size.height - 10, width: size.width + 10, height: size.height + 4)
        if r.minY < 0 { r.origin.y = hole.minY + 6; r.origin.x += 6 }
        RecordingStyle.back.setFill()
        NSBezierPath(roundedRect: r, xRadius: 3, yRadius: 3).fill()
        text.draw(at: CGPoint(x: r.minX + 5, y: r.minY + 2), withAttributes: attrs)
    }

    // MARK: - Mouse

    private func point(_ event: NSEvent) -> CGPoint { convert(event.locationInWindow, from: nil) }

    override func mouseDown(with event: NSEvent) {
        let p = point(event)
        down = p
        start = selection ?? .zero
        handle = selection.map { hitHandle($0, p) } ?? -1
        drag = handle >= 0 ? .resize : selection?.contains(p) == true ? .move : .pending
        bar.isHidden = true
        if drag == .pending { session?.selectionStarted(on: self) }
    }

    override func mouseDragged(with event: NSEvent) {
        let p = point(event)
        let old = shown
        let d = CGSize(width: p.x - down.x, height: p.y - down.y)
        switch drag {
        case .none: return
        case .pending:
            if abs(d.width) + abs(d.height) < 4 { return }
            drag = .new
            hover = nil
            selection = Geometry.normalize(down, p).intersection(bounds)
        case .new: selection = Geometry.normalize(down, p).intersection(bounds)
        case .move: selection = SelectionMath.move(start, by: d, within: bounds)
        case .resize: selection = Geometry.resize(start, handle: handle, dx: d.width, dy: d.height).intersection(bounds)
        }
        refresh(old)
    }

    override func mouseUp(with event: NSEvent) {
        guard drag != .none else { return }
        let old = shown
        let tiny = selection.map { $0.width < 8 || $0.height < 8 } ?? true
        if drag == .pending || drag == .new && tiny { selection = windowAt(down) ?? bounds }
        else if tiny { selection = start }
        drag = .none
        hover = nil
        refresh(old)
        settle()
    }

    override func rightMouseDown(with event: NSEvent) {
        if drag != .none {
            let old = shown
            selection = start == .zero ? nil : start
            drag = .none
            hover = nil
            refresh(old)
            if selection != nil { settle() }
        } else {
            session?.close(reason: "right-click")
        }
    }

    override func mouseMoved(with event: NSEvent) {
        let p = point(event)
        guard let sel = selection else {
            let w = windowAt(p)
            if w != hover {
                let old = hover
                hover = w
                refresh(old)
            }
            NSCursor.crosshair.set()
            return
        }
        cursor(for: p, sel).set()
    }

    private func settle() {
        guard let sel = selection else { return }
        bar.isHidden = false
        let visible = screen.visibleFrame
        let work = CGRect(x: visible.minX - screen.frame.minX, y: screen.frame.maxY - visible.maxY, width: visible.width, height: visible.height)
        bar.setFrameOrigin(SelectionMath.placeBar(area: sel, visible: work, size: bar.frame.size, gap: 8))
        window?.makeFirstResponder(self)
    }

    private func windowAt(_ p: CGPoint) -> CGRect? { windowRects.first { $0.contains(p) } }

    private func hitHandle(_ s: CGRect, _ p: CGPoint) -> Int {
        Geometry.handlePoints(s).firstIndex { abs($0.x - p.x) <= 7 && abs($0.y - p.y) <= 7 } ?? -1
    }

    private func cursor(for p: CGPoint, _ s: CGRect) -> NSCursor {
        switch hitHandle(s, p) {
        case 1, 5: return .resizeUpDown
        case 3, 7: return .resizeLeftRight
        case 0, 2, 4, 6: return .crosshair
        default: return s.contains(p) ? .openHand : .crosshair
        }
    }

    // MARK: - Keys

    override func keyDown(with event: NSEvent) {
        let cmd = event.modifierFlags.contains(.command)
        switch event.keyCode {
        case 53: session?.close(reason: "escape"); return
        case 36, 76: record(); return
        default: break
        }
        if !cmd, let key = event.charactersIgnoringModifiers?.lowercased() {
            if key == "s" { bar.toggleSystemSound(); return }
            if key == "m" { bar.toggleMicrophone(); return }
        }
        super.keyDown(with: event)
    }

    /// ⌘A (a menu item, so App Shortcuts apply): the whole display.
    @objc func snipSelectAll(_ sender: Any?) {
        let old = shown
        selection = bounds
        hover = nil
        refresh(old)
        settle()
    }

    private func record() {
        guard let area = selection ?? hover else { return }
        session?.record(self, area: area)
    }
}

/// Sound and microphone toggles, Record and Cancel, next to the selection.
final class RecordSetupBar: NSView {
    var onRecord: (() -> Void)?
    var onCancel: (() -> Void)?
    private let sound = BarButton(symbol: "speaker.wave.2", size: 30)
    private let mic = BarButton(symbol: "mic", size: 30)
    private let recordButton = RecordButton()
    private let cancel = BarButton(symbol: "xmark", size: 30)

    init() {
        let pad: CGFloat = 3
        super.init(frame: CGRect(x: 0, y: 0, width: pad + 64 + RecordButton.width + 4 + 30 + pad, height: 36))
        recordButton.frame.origin = CGPoint(x: pad + 64, y: pad)
        wantsLayer = true
        layer?.cornerRadius = 6
        layer?.backgroundColor = RecordingStyle.back.cgColor
        sound.frame.origin = CGPoint(x: pad, y: pad)
        mic.frame.origin = CGPoint(x: pad + 31, y: pad)
        cancel.frame.origin = CGPoint(x: recordButton.frame.maxX + 4, y: pad)
        sound.toolTip = "System sound on/off (S)"
        mic.toolTip = "Microphone on/off (M)"
        recordButton.toolTip = "Start recording (Return)"
        cancel.toolTip = "Cancel (Esc)"
        sound.action = { [weak self] in self?.toggleSystemSound() }
        mic.action = { [weak self] in self?.toggleMicrophone() }
        recordButton.action = { [weak self] in self?.onRecord?() }
        cancel.action = { [weak self] in self?.onCancel?() }
        for v in [sound, mic, recordButton, cancel] as [NSView] { addSubview(v) }
        sync()
    }

    required init?(coder: NSCoder) { fatalError("not used") }

    override func mouseDown(with event: NSEvent) {}

    func toggleSystemSound() {
        Settings.shared.recordSystemSound.toggle()
        sync()
    }

    func toggleMicrophone() {
        Settings.shared.recordMicrophone.toggle()
        sync()
    }

    private func sync() {
        sound.on = Settings.shared.recordSystemSound
        mic.on = Settings.shared.recordMicrophone
    }
}

final class RecordButton: NSView {
    var action: (() -> Void)?
    private var hover = false
    private static let font = NSFont.systemFont(ofSize: 13, weight: .medium)
    static let width = ("Record" as NSString).size(withAttributes: [.font: font]).width.rounded(.up) + 36

    init() {
        super.init(frame: CGRect(x: 0, y: 0, width: Self.width, height: 30))
        addTrackingArea(NSTrackingArea(rect: .zero, options: [.mouseEnteredAndExited, .activeAlways, .inVisibleRect], owner: self))
    }

    required init?(coder: NSCoder) { fatalError("not used") }

    override func mouseEntered(with event: NSEvent) { hover = true; needsDisplay = true }
    override func mouseExited(with event: NSEvent) { hover = false; needsDisplay = true }
    override func mouseDown(with event: NSEvent) {}
    override func mouseUp(with event: NSEvent) {
        if bounds.contains(convert(event.locationInWindow, from: nil)) { action?() }
    }
    override func acceptsFirstMouse(for event: NSEvent?) -> Bool { true }

    override func draw(_ dirtyRect: NSRect) {
        (hover ? NSColor(srgbRed: 0.91, green: 0.19, blue: 0.19, alpha: 1) : NSColor(srgbRed: 0.8, green: 0.15, blue: 0.15, alpha: 1)).setFill()
        NSBezierPath(roundedRect: bounds, xRadius: 4, yRadius: 4).fill()
        NSColor.white.setFill()
        NSBezierPath(ovalIn: CGRect(x: 10, y: bounds.midY - 5, width: 10, height: 10)).fill()
        let attrs: [NSAttributedString.Key: Any] = [.font: Self.font, .foregroundColor: NSColor.white]
        let size = ("Record" as NSString).size(withAttributes: attrs)
        ("Record" as NSString).draw(at: CGPoint(x: 26, y: bounds.midY - size.height / 2), withAttributes: attrs)
    }
}
