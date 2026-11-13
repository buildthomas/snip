import AppKit
import SnipCore
import UniformTypeIdentifiers

enum RecordingStyle {
    static let red = NSColor(srgbRed: 0.9, green: 0.16, blue: 0.16, alpha: 1)
    static let back = NSColor(srgbRed: 0.125, green: 0.125, blue: 0.133, alpha: 0.96)
    static let accent = NSColor(srgbRed: 0, green: 0.59, blue: 1, alpha: 1)

    /// Global AppKit frame (bottom-left origin) of a rect given in a screen's top-left point space.
    static func globalFrame(_ r: CGRect, on screen: NSScreen) -> CGRect {
        CGRect(x: screen.frame.minX + r.minX, y: screen.frame.maxY - r.maxY, width: r.width, height: r.height)
    }

    static func symbol(_ name: String, _ tip: String, size: CGFloat = 14, color: NSColor = .white) -> NSImage? {
        let config = NSImage.SymbolConfiguration(pointSize: size, weight: .regular).applying(.init(paletteColors: [color]))
        return NSImage(systemSymbolName: name, accessibilityDescription: tip)?.withSymbolConfiguration(config)
    }
}

/// Chrome shown while recording; never activates, sits above normal windows, kept out of captures.
class ChromePanel: NSPanel {
    init(frame: CGRect) {
        super.init(contentRect: frame, styleMask: [.borderless, .nonactivatingPanel], backing: .buffered, defer: false)
        level = .statusBar
        collectionBehavior = [.canJoinAllSpaces, .fullScreenAuxiliary, .stationary, .ignoresCycle]
        isOpaque = false
        backgroundColor = .clear
        hasShadow = false
        isReleasedWhenClosed = false
        hidesOnDeactivate = false
        sharingType = .none
    }
    required init?(coder: NSCoder) { fatalError("not used") }
    override var canBecomeKey: Bool { false }
    override var canBecomeMain: Bool { false }
}

/// One recording: the recorder, a thin red frame around the area and the floating Stop bar.
final class RecordingController {
    private let recorder: ScreenRecorder
    private let border: ChromePanel
    private let bar: RecordingBar
    private var done = false
    private var warmup: MicWarmup?
    var onEnded: (() -> Void)?

    init(recorder: ScreenRecorder, warmup: MicWarmup?) {
        self.recorder = recorder
        self.warmup = warmup
        let frame = RecordingStyle.globalFrame(recorder.area, on: recorder.screen)
        let t: CGFloat = 2
        border = ChromePanel(frame: frame.insetBy(dx: -t, dy: -t))
        border.ignoresMouseEvents = true
        border.contentView = BorderView(thickness: t)
        bar = RecordingBar(systemSound: recorder.systemSound, microphone: recorder.microphone, hotkey: Settings.shared.recordHotkey.display)
        bar.onStop = { [weak self] in self?.stop() }
        bar.onDiscard = { [weak self] in self?.discard() }
        bar.place(near: recorder.area, on: recorder.screen)
        recorder.onStarted = { [weak self] in
            self?.bar.markStarted()
            // The recorder's own microphone session is running by now; give it a moment before letting go.
            DispatchQueue.main.asyncAfter(deadline: .now() + 1) { self?.stopWarmup() }
        }
        recorder.onFailed = { [weak self] message in self?.fail(message) }
        recorder.onAutoStopped = { [weak self] _ in self?.stop() }
    }

    func start() {
        border.orderFrontRegardless()
        bar.orderFrontRegardless()
        recorder.start()
    }

    private func closeChrome() {
        border.orderOut(nil)
        bar.close()
        stopWarmup()
    }

    private func stopWarmup() {
        warmup?.stop()
        warmup = nil
    }

    private func fail(_ message: String) {
        guard !done else { return }
        done = true
        closeChrome()
        onEnded?()
        alert("Couldn't start recording.\n\n\(message)")
    }

    func stop() {
        guard !done else { return }
        done = true
        closeChrome()
        recorder.stop { [weak self] result in
            self?.onEnded?()
            switch result {
            case .success(let r): RecordingResultWindow.show(r)
            case .failure(let error): alert("The recording failed.\n\n\(error.localizedDescription)")
            }
        }
    }

    private func discard() {
        guard !done else { return }
        done = true
        closeChrome()
        recorder.abort()
        onEnded?()
    }

    /// Snip is quitting.
    func abort() {
        guard !done else { return }
        done = true
        closeChrome()
        recorder.abort()
    }
}

private func alert(_ text: String) {
    let a = NSAlert()
    a.messageText = "Snip"
    a.informativeText = text
    NSApp.activate(ignoringOtherApps: true)
    a.runModal()
}

private final class BorderView: NSView {
    private let thickness: CGFloat
    init(thickness: CGFloat) {
        self.thickness = thickness
        super.init(frame: .zero)
    }
    required init?(coder: NSCoder) { fatalError("not used") }

    override func draw(_ dirtyRect: NSRect) {
        RecordingStyle.red.setFill()
        let path = NSBezierPath(rect: bounds)
        path.appendRect(bounds.insetBy(dx: thickness, dy: thickness))
        path.windingRule = .evenOdd
        path.fill()
    }
}

/// Elapsed time, sound icons, Stop and Discard. Drag it anywhere.
final class RecordingBar: ChromePanel {
    var onStop: (() -> Void)?
    var onDiscard: (() -> Void)?
    private let view: BarView
    private var timer: Timer?

    init(systemSound: Bool, microphone: Bool, hotkey: String) {
        let v = BarView(systemSound: systemSound, microphone: microphone)
        view = v
        super.init(frame: CGRect(origin: .zero, size: v.frame.size))
        isMovableByWindowBackground = true
        contentView = view
        view.stop.action = { [weak self] in self?.onStop?() }
        view.discard.action = { [weak self] in self?.onDiscard?() }
        view.stop.toolTip = "Stop recording (\(hotkey))"
        view.discard.toolTip = "Discard recording"
        view.toolTip = "Recording - drag to move, \(hotkey) to stop"
        timer = Timer.scheduledTimer(withTimeInterval: 0.5, repeats: true) { [weak self] _ in
            guard let self, let start = self.startedAt else { return }
            self.view.elapsed = Date().timeIntervalSince(start)
        }
    }

    private var startedAt: Date?
    required init?(coder: NSCoder) { fatalError("not used") }

    func markStarted() {
        startedAt = Date()
        view.started = true
    }

    func place(near area: CGRect, on screen: NSScreen) {
        let visible = screen.visibleFrame
        let work = CGRect(x: visible.minX - screen.frame.minX, y: screen.frame.maxY - visible.maxY, width: visible.width, height: visible.height)
        let p = SelectionMath.placeBar(area: area, visible: work, size: frame.size, gap: 8)
        setFrame(RecordingStyle.globalFrame(CGRect(origin: p, size: frame.size), on: screen), display: false)
    }

    override func close() {
        timer?.invalidate()
        timer = nil
        super.close()
    }
}

/// A small clickable icon for the bars.
final class BarButton: NSView {
    var action: (() -> Void)?
    var symbol: String
    var on = false { didSet { needsDisplay = true } }
    private var hover = false

    init(symbol: String, size: CGFloat = 28) {
        self.symbol = symbol
        super.init(frame: CGRect(x: 0, y: 0, width: size, height: size))
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
    override var mouseDownCanMoveWindow: Bool { false }

    override func draw(_ dirtyRect: NSRect) {
        if on || hover {
            (on ? RecordingStyle.accent : NSColor.white.withAlphaComponent(0.27)).setFill()
            NSBezierPath(roundedRect: bounds.insetBy(dx: 1, dy: 1), xRadius: 4, yRadius: 4).fill()
        }
        guard let img = RecordingStyle.symbol(symbol, toolTip ?? "") else { return }
        img.draw(in: CGRect(x: bounds.midX - img.size.width / 2, y: bounds.midY - img.size.height / 2, width: img.size.width, height: img.size.height))
    }
}

private final class BarView: NSView {
    let stop = BarButton(symbol: "stop.fill")
    let discard = BarButton(symbol: "xmark")
    private let icons: [String]
    var started = false { didSet { needsDisplay = true } }
    var elapsed: Double = 0 { didSet { needsDisplay = true } }

    init(systemSound: Bool, microphone: Bool) {
        let list = (systemSound ? ["speaker.wave.2"] : []) + (microphone ? ["mic"] : [])
        icons = list
        let width = 140 + CGFloat(list.count) * 20
        super.init(frame: CGRect(x: 0, y: 0, width: width, height: 34))
        wantsLayer = true
        layer?.cornerRadius = 6
        layer?.backgroundColor = RecordingStyle.back.cgColor
        stop.frame.origin = CGPoint(x: width - 64, y: 3)
        discard.frame.origin = CGPoint(x: width - 33, y: 3)
        addSubview(stop)
        addSubview(discard)
    }
    required init?(coder: NSCoder) { fatalError("not used") }

    override func draw(_ dirtyRect: NSRect) {
        (started ? RecordingStyle.red : NSColor(white: 0.47, alpha: 1)).setFill()
        NSBezierPath(ovalIn: CGRect(x: 11, y: bounds.midY - 5, width: 10, height: 10)).fill()
        let attrs: [NSAttributedString.Key: Any] = [.font: NSFont.monospacedDigitSystemFont(ofSize: 13, weight: .regular), .foregroundColor: NSColor.white]
        let text = RecordingMath.formatDuration(elapsed) as NSString
        text.draw(at: CGPoint(x: 28, y: bounds.midY - text.size(withAttributes: attrs).height / 2), withAttributes: attrs)
        for (i, name) in icons.enumerated() {
            guard let img = RecordingStyle.symbol(name, name, size: 12, color: NSColor(white: 0.8, alpha: 1)) else { continue }
            let x = stop.frame.minX - CGFloat(icons.count - i) * 20 + (20 - img.size.width) / 2
            img.draw(in: CGRect(x: x, y: bounds.midY - img.size.height / 2, width: img.size.width, height: img.size.height))
        }
    }
}

/// After a recording: last frame, length and size, and Copy (file), Save as, Play or Discard.
final class RecordingResultWindow: NSWindow, NSWindowDelegate {
    private static var openWindows: [RecordingResultWindow] = []
    private let result: RecordingResult
    private var resolved = false
    private var copied = false

    static func show(_ result: RecordingResult) {
        let w = RecordingResultWindow(result)
        openWindows.append(w)
        NSApp.activate(ignoringOtherApps: true)
        w.center()
        w.makeKeyAndOrderFront(nil)
    }

    /// Snip is quitting: recordings nobody saved or copied are deleted.
    static func discardAllOnQuit() {
        for w in openWindows where !w.resolved && !w.copied { RecordingFiles.delete(w.result.url) }
        openWindows.removeAll()
    }

    private init(_ result: RecordingResult) {
        self.result = result
        let width: CGFloat = 480, imageH: CGFloat = 270
        let reasonH: CGFloat = result.stopReason == nil ? 0 : 24
        super.init(contentRect: CGRect(x: 0, y: 0, width: width, height: imageH + 28 + reasonH + 48),
                   styleMask: [.titled, .closable], backing: .buffered, defer: false)
        title = "Snip recording"
        isReleasedWhenClosed = false
        level = .floating
        delegate = self
        appearance = NSAppearance(named: .darkAqua)

        let content = NSView(frame: contentLayoutRect)
        let image = NSImageView(frame: CGRect(x: 0, y: content.bounds.height - imageH, width: width, height: imageH))
        image.imageScaling = .scaleProportionallyUpOrDown
        image.wantsLayer = true
        image.layer?.backgroundColor = NSColor.black.cgColor
        if let frame = result.lastFrame { image.image = NSImage(cgImage: frame, size: .zero) }
        content.addSubview(image)

        let info = NSTextField(labelWithString: "\(RecordingMath.formatDuration(result.duration)) - \(RecordingMath.formatSize(result.bytes)) - MP4")
        info.frame = CGRect(x: 10, y: image.frame.minY - 24, width: width - 20, height: 18)
        content.addSubview(info)
        if let reason = result.stopReason {
            let label = NSTextField(labelWithString: reason)
            label.textColor = NSColor(srgbRed: 1, green: 0.67, blue: 0.24, alpha: 1)
            label.frame = CGRect(x: 10, y: info.frame.minY - 24, width: width - 20, height: 18)
            content.addSubview(label)
        }

        var x = width - 10
        func button(_ title: String, _ tip: String, _ action: Selector, key: String = "") {
            let b = NSButton(title: title, target: self, action: action)
            b.bezelStyle = .rounded
            b.toolTip = tip
            b.keyEquivalent = key
            b.sizeToFit()
            b.frame.size.width = max(b.frame.width, 90)
            x -= b.frame.width
            b.frame.origin = CGPoint(x: x, y: 10)
            x -= 6
            content.addSubview(b)
        }
        button("Copy", "Copy the video file to paste into chats and documents (⌘C)", #selector(snipCopy(_:)), key: "\r")
        button("Save as…", "Save as MP4 (⌘S)", #selector(snipSave(_:)))
        button("Play", "Open in your video player", #selector(play))
        button("Discard", "Delete this recording", #selector(discard))
        contentView = content
    }
    required init?(coder: NSCoder) { fatalError("not used") }

    @objc func snipCopy(_ sender: Any?) {
        let pb = NSPasteboard.general
        pb.clearContents()
        guard pb.writeObjects([result.url as NSURL]) else {
            message("Couldn't copy to the clipboard.")
            return
        }
        Log.info("recording copied as a file")
        copied = true
        resolved = true
        close()
    }

    @objc func snipSave(_ sender: Any?) {
        let panel = NSSavePanel()
        panel.title = "Save recording"
        panel.allowedContentTypes = [.mpeg4Movie]
        panel.nameFieldStringValue = result.url.lastPathComponent
        let dir = Settings.shared.lastSaveDirectory
        panel.directoryURL = (dir.map { FileManager.default.fileExists(atPath: $0.path) } ?? false) ? dir
            : FileManager.default.urls(for: .moviesDirectory, in: .userDomainMask).first
        panel.beginSheetModal(for: self) { [weak self] response in
            guard let self, response == .OK, let dest = panel.url else { return }
            do {
                if FileManager.default.fileExists(atPath: dest.path) { try FileManager.default.removeItem(at: dest) }
                try FileManager.default.copyItem(at: self.result.url, to: dest)
            } catch {
                Log.error("recording save failed: \(error)")
                self.message("Couldn't save \(dest.lastPathComponent).\n\n\(error.localizedDescription)")
                return
            }
            Settings.shared.lastSaveDirectory = dest.deletingLastPathComponent()
            Log.info("recording saved")
            if !self.copied { self.deleteFile() }
            self.resolved = true
            self.close()
        }
    }

    @objc private func play() {
        if !NSWorkspace.shared.open(result.url) { message("Couldn't open the video.") }
    }

    @objc private func discard() {
        deleteFile()
        resolved = true
        close()
    }

    override func cancelOperation(_ sender: Any?) { performClose(nil) }

    func windowShouldClose(_ sender: NSWindow) -> Bool {
        if resolved { return true }
        let a = NSAlert()
        a.messageText = "Discard this recording?"
        a.addButton(withTitle: "Keep")
        a.addButton(withTitle: "Discard")
        a.beginSheetModal(for: self) { [weak self] response in
            guard let self, response == .alertSecondButtonReturn else { return }
            self.discard()
        }
        return false
    }

    func windowWillClose(_ notification: Notification) {
        Self.openWindows.removeAll { $0 === self }
    }

    private func deleteFile() {
        if let error = RecordingFiles.delete(result.url) {
            message("Couldn't delete the recording (is it open in a player?). It stays in the temp folder until Snip next starts.\n\n\(error)")
        }
    }

    private func message(_ text: String) {
        let a = NSAlert()
        a.messageText = "Snip"
        a.informativeText = text
        a.beginSheetModal(for: self, completionHandler: nil)
    }
}
