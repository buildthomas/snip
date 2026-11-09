import AppKit
import SnipCore

/// Borderless full-screen window per display, above everything (including full-screen apps and the menu bar).
final class OverlayWindow: NSWindow {
    override var canBecomeKey: Bool { true }
    override var canBecomeMain: Bool { true }
}

/// One capture session: an overlay per display; the selection lives on one of them.
final class OverlaySession {
    private(set) var windows: [OverlayWindow] = []
    private var views: [OverlayView] = []
    var onClose: (() -> Void)?
    private var closed = false

    init(captures: [DisplayCapture]) {
        for capture in captures {
            let frame = capture.screen.frame
            let window = OverlayWindow(contentRect: frame, styleMask: .borderless, backing: .buffered, defer: false)
            window.level = .screenSaver
            window.collectionBehavior = [.canJoinAllSpaces, .fullScreenAuxiliary, .stationary, .ignoresCycle]
            window.isOpaque = true
            window.hasShadow = false
            window.backgroundColor = .black
            window.isReleasedWhenClosed = false
            window.acceptsMouseMovedEvents = true
            let view = OverlayView(frame: CGRect(origin: .zero, size: frame.size), capture: capture,
                                   windowRects: Capture.windowRects(for: capture.screen))
            view.session = self
            window.contentView = view
            window.setFrame(frame, display: false)
            windows.append(window)
            views.append(view)
        }
    }

    func show() {
        let start = DispatchTime.now()
        NSApp.activate(ignoringOtherApps: true)
        for w in windows { w.orderFrontRegardless() }
        let mouse = NSEvent.mouseLocation
        let key = windows.first { $0.frame.contains(mouse) } ?? windows.first
        key?.makeKeyAndOrderFront(nil)
        key?.makeFirstResponder(key?.contentView)
        Log.debug("overlay shown on \(windows.count) display(s) in \(Log.ms(since: start)) ms, key display: \(key?.screen?.localizedName ?? "?")")
    }

    /// A new selection on one display clears the others.
    func selectionStarted(on view: OverlayView) {
        for v in views where v !== view { v.clearSelection() }
        view.window?.makeKeyAndOrderFront(nil)
    }

    func close(reason: String) {
        guard !closed else { return }
        closed = true
        Log.info("overlay closed: \(reason)")
        for w in windows { w.orderOut(nil); w.contentView = nil }
        windows.removeAll()
        views.removeAll()
        onClose?()
    }
}

private enum Drag { case none, pending, newSelection, move, resize, draw, moveText }

private enum ButtonKind { case tool, color, undo, redo, copy, save, close, redactStyle, redactShape, redactSecure, emoji }

private final class Button {
    let kind: ButtonKind
    let tool: Tool
    let option: Int
    var tip: String
    let symbol: String
    var rect: CGRect = .zero
    init(_ kind: ButtonKind, tool: Tool = .none, option: Int = 0, tip: String, symbol: String = "") {
        self.kind = kind; self.tool = tool; self.option = option; self.tip = tip; self.symbol = symbol
    }
}

final class OverlayView: NSView, NSMenuItemValidation {
    weak var session: OverlaySession?
    let capture: DisplayCapture
    private let windowRects: [CGRect]
    private var env: RenderEnv { RenderEnv(source: capture.image, scale: capture.scale) }

    private var selection: CGRect?
    private var hoverWindow: CGRect?
    private var drag = Drag.none
    private var dragStart = CGPoint.zero
    private var dragOrigSel = CGRect.zero
    private var selectionBeforeDrag: CGRect?
    private var resizeHandle = 0

    private var annotations: [Annotation] = []
    private var redoStack: [Annotation] = []
    private var current: Annotation?
    private var editing: TextMark?
    private var textGrab = CGPoint.zero
    private var textOriginBefore = CGPoint.zero

    private let settings = Settings.shared
    private var tool = Tool.none
    private var color: NSColor
    private var size: Int
    private var redactStyle: RedactStyle
    private var redactShape: RedactShape
    private var redactSecure: Bool
    private var redactCell: Int
    private var emojiIndex: Int

    private var toolButtons: [Button] = []
    private var actionButtons: [Button] = []
    private var redactButtons: [Button] = []
    private var emojiButtons: [Button] = []
    private var toolBar = CGRect.zero, actionBar = CGRect.zero, paletteRect = CGRect.zero, redactBar = CGRect.zero, emojiPanel = CGRect.zero
    private var cellReadout = CGRect.zero
    private var paletteOpen = false
    private var hoverButton: Button?
    private var previewAt: CGPoint?
    private var scrollAccum: CGFloat = 0
    private var bubble: (text: String, at: CGPoint, until: Date)?

    private static let accent = NSColor(srgbRed: 0, green: 0.59, blue: 1, alpha: 1)
    private static let barBack = NSColor(srgbRed: 0.125, green: 0.125, blue: 0.133, alpha: 0.94)
    private static let warning = NSColor(srgbRed: 0.9, green: 0.47, blue: 0, alpha: 1)
    private static let buttonSize: CGFloat = 30

    init(frame: CGRect, capture: DisplayCapture, windowRects: [CGRect]) {
        self.capture = capture
        self.windowRects = windowRects
        color = settings.color
        size = settings.size
        redactStyle = settings.redactStyle
        redactShape = settings.redactShape
        redactSecure = settings.redactSecure
        redactCell = settings.redactCell
        emojiIndex = settings.emojiIndex
        super.init(frame: frame)
        buildButtons()
        addTrackingArea(NSTrackingArea(rect: .zero, options: [.mouseMoved, .activeAlways, .inVisibleRect, .mouseEnteredAndExited], owner: self))
    }

    required init?(coder: NSCoder) { fatalError("not used") }

    override var isFlipped: Bool { true }
    override var acceptsFirstResponder: Bool { true }
    override func acceptsFirstMouse(for event: NSEvent?) -> Bool { true }

    func clearSelection() {
        selection = nil
        annotations.removeAll(); redoStack.removeAll(); current = nil; editing = nil
        tool = .none
        needsDisplay = true
    }

    // MARK: - Buttons and layout

    private func buildButtons() {
        func t(_ tool: Tool, _ tip: String, _ symbol: String) { toolButtons.append(Button(.tool, tool: tool, tip: tip, symbol: symbol)) }
        t(.pen, "Pen (P)", "pencil.tip")
        t(.line, "Line (L)", "line.diagonal")
        t(.arrow, "Arrow (A)", "arrow.up.right")
        t(.rectangle, "Rectangle (R)", "rectangle")
        t(.ellipse, "Ellipse (E)", "circle")
        t(.highlighter, "Highlighter (H)", "highlighter")
        t(.text, "Text (T)", "textformat")
        t(.number, "Numbered step (N)", "1.circle")
        t(.emoji, "Emoji stamp (M): drag while holding to rotate", "")
        t(.redact, "Redact (B)", "eye.slash")
        toolButtons.append(Button(.color, tip: "Color (scroll = size)"))
        toolButtons.append(Button(.undo, tip: "Undo (Cmd+Z)", symbol: "arrow.uturn.backward"))
        toolButtons.append(Button(.redo, tip: "Redo (Cmd+Shift+Z)", symbol: "arrow.uturn.forward"))

        redactButtons.append(Button(.redactShape, option: RedactShape.brush.rawValue, tip: "Brush (F switches shape, scroll = width)", symbol: "paintbrush.pointed"))
        redactButtons.append(Button(.redactShape, option: RedactShape.box.rawValue, tip: "Box (F switches shape)", symbol: "rectangle.dashed"))
        redactButtons.append(Button(.redactStyle, option: RedactStyle.black.rawValue, tip: "Black (always secure)", symbol: "square.fill"))
        redactButtons.append(Button(.redactStyle, option: RedactStyle.pixelate.rawValue, tip: "Pixelate (Option+scroll = cell size)", symbol: "square.grid.3x3.fill"))
        redactButtons.append(Button(.redactStyle, option: RedactStyle.blur.rawValue, tip: "Blur (Option+scroll = blur size)", symbol: "drop.fill"))
        redactButtons.append(Button(.redactSecure, tip: ""))
        updateSecureTip()

        for (i, _) in EmojiSet.all.enumerated() { emojiButtons.append(Button(.emoji, option: i, tip: "")) }

        actionButtons.append(Button(.copy, tip: "Copy (Cmd+C)", symbol: "doc.on.doc"))
        actionButtons.append(Button(.save, tip: "Save as (Cmd+S)", symbol: "square.and.arrow.down"))
        actionButtons.append(Button(.close, tip: "Close (Esc)", symbol: "xmark"))
    }

    private func updateSecureTip() {
        redactButtons.last?.tip = redactSecure
            ? "Secure: generated, nothing underneath can be recovered. Click for insecure"
            : "INSECURE: real pixelate/blur of the content, can be partly reversed. Click for secure"
    }

    private var redactBarVisible: Bool { tool == .redact && selection != nil }
    private var emojiPanelVisible: Bool { tool == .emoji && selection != nil }

    private func layoutChrome(_ sel: CGRect) {
        let b = Self.buttonSize, gap: CGFloat = 6, pad: CGFloat = 3
        let screen = bounds

        let toolH = CGFloat(toolButtons.count) * b + pad * 2 + 8, toolW = b + pad * 2
        var tx = sel.maxX + gap
        if tx + toolW > screen.maxX { tx = sel.minX - gap - toolW }
        if tx < screen.minX { tx = sel.maxX - toolW - gap }
        let ty = min(max(sel.minY, screen.minY), max(screen.minY, screen.maxY - toolH))
        toolBar = CGRect(x: tx, y: ty, width: toolW, height: toolH)
        var y = ty + pad
        for btn in toolButtons {
            if btn.kind == .color || btn.kind == .undo { y += 4 }
            btn.rect = CGRect(x: tx + pad, y: y, width: b, height: b)
            y += b
        }

        let actW = CGFloat(actionButtons.count) * b + pad * 2, actH = b + pad * 2
        let ax = min(max(sel.maxX - actW, screen.minX), max(screen.minX, screen.maxX - actW))
        var ay = sel.maxY + gap
        if ay + actH > screen.maxY { ay = sel.minY - gap - actH }
        if ay < screen.minY { ay = sel.maxY - actH - gap }
        actionBar = CGRect(x: ax, y: ay, width: actW, height: actH)
        for (i, btn) in actionButtons.enumerated() { btn.rect = CGRect(x: ax + pad + CGFloat(i) * b, y: ay + pad, width: b, height: b) }

        let toolbarLeftOfSelection = toolBar.maxX <= sel.minX + 1
        func sideX(_ width: CGFloat) -> CGFloat {
            var x = toolbarLeftOfSelection ? toolBar.minX - gap - width : toolBar.maxX + gap
            if x + width > screen.maxX { x = toolBar.minX - gap - width }
            if x < screen.minX { x = toolBar.maxX + gap }
            return x
        }

        let colorBtn = toolButtons.first { $0.kind == .color }!.rect
        let sw: CGFloat = 24, cols = 6, rows = (Palette.colors.count + cols - 1) / cols
        let pw = CGFloat(cols) * sw + pad * 2, ph = CGFloat(rows) * sw + pad * 2
        paletteRect = CGRect(x: sideX(pw), y: min(max(colorBtn.minY, screen.minY), screen.maxY - ph), width: pw, height: ph)

        let redactBtn = toolButtons.first { $0.tool == .redact }!.rect
        let sep: CGFloat = 8, readoutW: CGFloat = 46
        let rw = CGFloat(redactButtons.count) * b + 2 * sep + pad * 2 + readoutW
        let rx = sideX(rw)
        redactBar = CGRect(x: rx, y: redactBtn.minY - pad, width: rw, height: b + pad * 2)
        for (i, btn) in redactButtons.enumerated() {
            let group: CGFloat = btn.kind == .redactStyle ? 1 : btn.kind == .redactSecure ? 2 : 0
            btn.rect = CGRect(x: rx + pad + CGFloat(i) * b + group * sep, y: redactBtn.minY, width: b, height: b)
        }
        cellReadout = CGRect(x: redactButtons.last!.rect.maxX + 2, y: redactBtn.minY, width: readoutW, height: b)

        let emojiBtn = toolButtons.first { $0.tool == .emoji }!.rect
        let ecols = 8, erows = (emojiButtons.count + ecols - 1) / ecols
        let ew = CGFloat(ecols) * b + pad * 2, eh = CGFloat(erows) * b + pad * 2
        let ex = sideX(ew), ey = min(max(emojiBtn.minY - pad, screen.minY), max(screen.minY, screen.maxY - eh))
        emojiPanel = CGRect(x: ex, y: ey, width: ew, height: eh)
        for (i, btn) in emojiButtons.enumerated() {
            btn.rect = CGRect(x: ex + pad + CGFloat(i % ecols) * b, y: ey + pad + CGFloat(i / ecols) * b, width: b, height: b)
        }
    }

    private func swatchRect(_ i: Int) -> CGRect {
        let sw: CGFloat = 24, pad: CGFloat = 3
        return CGRect(x: paletteRect.minX + pad + CGFloat(i % 6) * sw, y: paletteRect.minY + pad + CGFloat(i / 6) * sw, width: sw, height: sw)
    }

    // MARK: - Drawing

    override func draw(_ dirtyRect: NSRect) {
        guard let ctx = NSGraphicsContext.current?.cgContext else { return }
        drawImageUpright(ctx, capture.image, in: bounds)

        let bright = selection ?? (drag == .none || drag == .pending ? hoverWindow : nil)
        let shade = CGMutablePath()
        shade.addRect(bounds)
        if let bright { shade.addRect(bright) }
        ctx.addPath(shade)
        ctx.setFillColor(NSColor.black.withAlphaComponent(0.47).cgColor)
        ctx.fillPath(using: .evenOdd)

        if let sel = selection {
            ctx.saveGState()
            ctx.clip(to: sel)
            for a in annotations { a.draw(ctx, env) }
            current?.draw(ctx, env)
            if let editing { drawEditing(ctx, editing) }
            if let at = previewAt, drag == .none { drawPreview(ctx, at) }
            ctx.restoreGState()
            layoutChrome(sel)
            drawChrome(ctx, sel)
        } else if let hw = hoverWindow, drag == .none || drag == .pending {
            ctx.setStrokeColor(Self.accent.cgColor)
            ctx.setLineWidth(2)
            ctx.stroke(hw.insetBy(dx: 1, dy: 1))
        }

        if let bubble, bubble.until > Date() { drawLabel(bubble.text, near: bubble.at) }
    }

    private func drawEditing(_ ctx: CGContext, _ note: TextMark) {
        note.draw(ctx, env)
        let sz = note.textSize()
        let lines = note.text.components(separatedBy: "\n")
        let lastWidth = ((lines.last ?? "") as NSString).size(withAttributes: note.attributes).width
        let lineH = note.fontSize * 1.2
        let caretX = note.origin.x + lastWidth + 1, caretY = note.origin.y + lineH * CGFloat(lines.count - 1)
        ctx.saveGState()
        ctx.setStrokeColor(note.color.cgColor)
        ctx.setLineWidth(max(1.5, note.size / 3))
        ctx.move(to: CGPoint(x: caretX, y: caretY)); ctx.addLine(to: CGPoint(x: caretX, y: caretY + lineH)); ctx.strokePath()
        ctx.setStrokeColor(NSColor.white.withAlphaComponent(0.63).cgColor)
        ctx.setLineWidth(1)
        ctx.setLineDash(phase: 0, lengths: [4, 3])
        ctx.stroke(CGRect(x: note.origin.x - 4, y: note.origin.y - 3, width: sz.width + 10, height: sz.height + 6))
        ctx.restoreGState()
    }

    private func drawChrome(_ ctx: CGContext, _ sel: CGRect) {
        ctx.saveGState()
        ctx.setStrokeColor(Self.accent.cgColor)
        ctx.setLineWidth(1)
        ctx.stroke(sel.insetBy(dx: -0.5, dy: -0.5))
        for h in Geometry.handlePoints(sel) {
            let r = CGRect(x: h.x - 3.5, y: h.y - 3.5, width: 7, height: 7)
            ctx.setFillColor(NSColor.white.cgColor); ctx.fill(r)
            ctx.stroke(r)
        }
        ctx.restoreGState()

        if drag != .draw {
            let px = CGSize(width: (sel.width * capture.scale).rounded(), height: (sel.height * capture.scale).rounded())
            drawLabel("\(Int(px.width)) × \(Int(px.height))", near: CGPoint(x: sel.minX, y: sel.minY - 24), anchorTopLeft: true)
        }
        if drag == .newSelection || drag == .resize { return }

        drawBar(toolBar, toolButtons)
        drawBar(actionBar, actionButtons)
        if redactBarVisible {
            drawBar(redactBar, redactButtons)
            NSColor.white.withAlphaComponent(0.35).setStroke()
            for kind in [ButtonKind.redactStyle, .redactSecure] {
                if let first = redactButtons.first(where: { $0.kind == kind })?.rect {
                    let p = NSBezierPath(); p.move(to: CGPoint(x: first.minX - 4, y: redactBar.minY + 6)); p.line(to: CGPoint(x: first.minX - 4, y: redactBar.maxY - 6)); p.stroke()
                }
            }
            let attrs: [NSAttributedString.Key: Any] = [.font: NSFont.systemFont(ofSize: 12), .foregroundColor: redactStyle == .black ? NSColor.gray : NSColor.white]
            let s = "\(redactCell) px" as NSString, sz = s.size(withAttributes: attrs)
            s.draw(at: CGPoint(x: cellReadout.midX - sz.width / 2, y: cellReadout.midY - sz.height / 2), withAttributes: attrs)
        }
        if emojiPanelVisible { drawBar(emojiPanel, emojiButtons) }
        if paletteOpen {
            Self.barBack.setFill(); NSBezierPath(roundedRect: paletteRect, xRadius: 5, yRadius: 5).fill()
            for (i, c) in Palette.colors.enumerated() {
                let r = swatchRect(i).insetBy(dx: 3, dy: 3)
                c.setFill(); NSBezierPath(rect: r).fill()
                let selected = c.hex == color.hex
                (selected ? NSColor.white : NSColor.white.withAlphaComponent(0.35)).setStroke()
                let p = NSBezierPath(rect: r); p.lineWidth = selected ? 2 : 1; p.stroke()
            }
        }
        if let hb = hoverButton, !hb.tip.isEmpty {
            let below = redactButtons.contains { $0 === hb } || actionButtons.contains { $0 === hb }
            let at = below ? CGPoint(x: hb.rect.minX, y: hb.rect.maxY + 8) : CGPoint(x: toolBar.maxX + 6, y: hb.rect.minY + 4)
            drawLabel(hb.tip, near: at, anchorTopLeft: true)
        }
    }

    private func drawBar(_ bar: CGRect, _ buttons: [Button]) {
        Self.barBack.setFill()
        NSBezierPath(roundedRect: bar, xRadius: 5, yRadius: 5).fill()
        for b in buttons {
            let active: Bool
            switch b.kind {
            case .tool: active = b.tool == tool
            case .color: active = paletteOpen
            case .redactStyle: active = b.option == redactStyle.rawValue
            case .redactShape: active = b.option == redactShape.rawValue
            case .redactSecure: active = true
            case .emoji: active = b.option == emojiIndex
            default: active = false
            }
            if active || b === hoverButton {
                let fill: NSColor = b.kind == .redactSecure && !redactSecure ? Self.warning : active ? Self.accent : NSColor.white.withAlphaComponent(0.27)
                fill.setFill()
                NSBezierPath(roundedRect: b.rect.insetBy(dx: 1, dy: 1), xRadius: 4, yRadius: 4).fill()
            }
            let enabled = b.kind == .undo ? (!annotations.isEmpty || editing != nil) : b.kind == .redo ? !redoStack.isEmpty : true
            drawIcon(b, enabled: enabled)
        }
    }

    private func drawIcon(_ b: Button, enabled: Bool) {
        let fg = enabled ? NSColor(white: 0.92, alpha: 1) : NSColor(white: 0.43, alpha: 1)
        switch b.kind {
        case .color:
            let r = b.rect.insetBy(dx: 7.5, dy: 7.5)
            color.setFill(); NSBezierPath(ovalIn: r).fill()
            fg.setStroke(); let p = NSBezierPath(ovalIn: r); p.lineWidth = 1.5; p.stroke()
        case .emoji:
            drawEmojiIcon(EmojiSet.all[b.option], in: b.rect)
        case .tool where b.tool == .emoji:
            drawEmojiIcon(EmojiSet.all[emojiIndex], in: b.rect)
        default:
            let name = b.kind == .redactSecure ? (redactSecure ? "lock.shield" : "exclamationmark.shield") : b.symbol
            let config = NSImage.SymbolConfiguration(pointSize: 14, weight: .regular).applying(.init(paletteColors: [fg]))
            guard let img = NSImage(systemSymbolName: name, accessibilityDescription: b.tip)?.withSymbolConfiguration(config) else { return }
            let r = CGRect(x: b.rect.midX - img.size.width / 2, y: b.rect.midY - img.size.height / 2, width: img.size.width, height: img.size.height)
            img.draw(in: r, from: .zero, operation: .sourceOver, fraction: 1, respectFlipped: true, hints: nil)
        }
    }

    private func drawEmojiIcon(_ emoji: String, in rect: CGRect) {
        let attrs: [NSAttributedString.Key: Any] = [.font: NSFont(name: "Apple Color Emoji", size: 17) ?? NSFont.systemFont(ofSize: 17)]
        let s = emoji as NSString, sz = s.size(withAttributes: attrs)
        s.draw(at: CGPoint(x: rect.midX - sz.width / 2, y: rect.midY - sz.height / 2), withAttributes: attrs)
    }

    private func drawLabel(_ text: String, near p: CGPoint, anchorTopLeft: Bool = false) {
        let attrs: [NSAttributedString.Key: Any] = [.font: NSFont.systemFont(ofSize: 12), .foregroundColor: NSColor.white]
        let s = text as NSString, sz = s.size(withAttributes: attrs)
        var r = CGRect(x: p.x, y: p.y, width: sz.width + 12, height: sz.height + 6)
        if !anchorTopLeft { r.origin = CGPoint(x: p.x + 18, y: p.y + 18) }
        if r.minY < bounds.minY { r.origin.y = bounds.minY + 4 }
        if r.maxX > bounds.maxX { r.origin.x = bounds.maxX - r.width - 4 }
        if r.maxY > bounds.maxY { r.origin.y = bounds.maxY - r.height - 4 }
        Self.barBack.setFill()
        NSBezierPath(roundedRect: r, xRadius: 4, yRadius: 4).fill()
        s.draw(at: CGPoint(x: r.minX + 6, y: r.minY + 3), withAttributes: attrs)
    }

    // MARK: - Hover preview

    private var previewDiameter: CGFloat? {
        let s = CGFloat(size)
        switch tool {
        case .pen, .line, .arrow, .rectangle, .ellipse: return s
        case .highlighter: return s * 4
        case .number: return (10 + s * 2) * 2
        case .emoji: return EmojiMark.points(for: s) * 1.5
        case .redact: return redactShape == .brush ? RedactMark.brushWidth(for: s) : CGFloat(redactCell) / capture.scale * 3
        default: return nil
        }
    }

    private func drawPreview(_ ctx: CGContext, _ at: CGPoint) {
        guard let d = previewDiameter else { return }
        switch tool {
        case .emoji:
            let m = EmojiMark(); m.emoji = EmojiSet.all[emojiIndex]; m.center = at; m.size = CGFloat(size); m.opacity = 0.55
            m.draw(ctx, env)
            return
        case .number:
            let m = NumberMark(); m.center = at; m.size = CGFloat(size); m.number = annotations.filter { $0 is NumberMark }.count + 1
            m.color = color.withAlphaComponent(0.55)
            m.draw(ctx, env)
            return
        default: break
        }
        let r = CGRect(x: at.x - max(d, 2) / 2, y: at.y - max(d, 2) / 2, width: max(d, 2), height: max(d, 2))
        let shape = CGMutablePath()
        if tool == .redact && redactShape == .box { shape.addRect(r) } else { shape.addEllipse(in: r) }
        ctx.saveGState()
        let fill: NSColor = tool == .redact ? NSColor.gray.withAlphaComponent(0.27)
            : tool == .highlighter ? color.withAlphaComponent(StrokeMark.highlighterAlpha) : color.withAlphaComponent(0.59)
        ctx.addPath(shape); ctx.setFillColor(fill.cgColor); ctx.fillPath()
        if tool == .redact && redactStyle != .black {
            ctx.addPath(shape); ctx.clip()
            let c = CGFloat(redactCell) / capture.scale
            ctx.setStrokeColor(NSColor.white.withAlphaComponent(0.6).cgColor); ctx.setLineWidth(1)
            var x = (r.minX / c).rounded(.down) * c
            while x <= r.maxX { ctx.move(to: CGPoint(x: x, y: r.minY)); ctx.addLine(to: CGPoint(x: x, y: r.maxY)); x += c }
            var y = (r.minY / c).rounded(.down) * c
            while y <= r.maxY { ctx.move(to: CGPoint(x: r.minX, y: y)); ctx.addLine(to: CGPoint(x: r.maxX, y: y)); y += c }
            ctx.strokePath()
        }
        ctx.restoreGState()
        ctx.saveGState()
        ctx.addPath(shape); ctx.setStrokeColor(NSColor.black.withAlphaComponent(0.78).cgColor); ctx.setLineWidth(1); ctx.strokePath()
        ctx.addPath(shape); ctx.setStrokeColor(NSColor.white.withAlphaComponent(0.9).cgColor); ctx.setLineDash(phase: 0, lengths: [3, 3]); ctx.strokePath()
        ctx.restoreGState()
    }

    // MARK: - Hit testing

    private func hitHandle(_ p: CGPoint) -> Int? {
        guard let sel = selection else { return nil }
        return Geometry.handlePoints(sel).firstIndex { abs($0.x - p.x) <= 6 && abs($0.y - p.y) <= 6 }
    }

    private func hitButton(_ p: CGPoint) -> Button? {
        guard selection != nil else { return nil }
        if let b = toolButtons.first(where: { $0.rect.contains(p) }) { return b }
        if let b = actionButtons.first(where: { $0.rect.contains(p) }) { return b }
        if redactBarVisible, let b = redactButtons.first(where: { $0.rect.contains(p) }) { return b }
        if emojiPanelVisible, let b = emojiButtons.first(where: { $0.rect.contains(p) }) { return b }
        return nil
    }

    private func onChrome(_ p: CGPoint) -> Bool {
        guard selection != nil else { return false }
        return toolBar.contains(p) || actionBar.contains(p) || (paletteOpen && paletteRect.contains(p))
            || (redactBarVisible && redactBar.contains(p)) || (emojiPanelVisible && emojiPanel.contains(p))
    }

    private func windowAt(_ p: CGPoint) -> CGRect? { windowRects.first { $0.contains(p) } }

    // MARK: - Mouse

    override func mouseDown(with event: NSEvent) {
        let p = convert(event.locationInWindow, from: nil)
        window?.makeFirstResponder(self)

        if paletteOpen {
            if paletteRect.contains(p) {
                for (i, c) in Palette.colors.enumerated() where swatchRect(i).contains(p) { setColor(c) }
                paletteOpen = false
                needsDisplay = true
                return
            }
            paletteOpen = false
        }
        if let b = hitButton(p) { press(b); return }
        if onChrome(p) { return }

        if let note = editing {
            if note.bounds.contains(p) {
                drag = .moveText
                textOriginBefore = note.origin
                textGrab = CGPoint(x: p.x - note.origin.x, y: p.y - note.origin.y)
                return
            }
            commitText()
        }

        dragStart = p
        if let sel = selection {
            if let h = hitHandle(p) {
                drag = .resize; resizeHandle = h; dragOrigSel = sel
                return
            }
            if sel.contains(p) {
                if tool != .none { previewAt = nil; beginDraw(p); return }
                drag = .move; dragOrigSel = sel
                return
            }
            if tool != .none { return }
        }
        selectionBeforeDrag = selection
        drag = .pending
    }

    override func mouseDragged(with event: NSEvent) {
        let p = convert(event.locationInWindow, from: nil)
        switch drag {
        case .pending:
            if abs(p.x - dragStart.x) > 3 || abs(p.y - dragStart.y) > 3 {
                drag = .newSelection
                hoverWindow = nil
                session?.selectionStarted(on: self)
                setSelection(Geometry.normalize(dragStart, p))
            }
        case .newSelection:
            setSelection(Geometry.normalize(dragStart, p))
        case .move:
            var r = dragOrigSel.offsetBy(dx: p.x - dragStart.x, dy: p.y - dragStart.y)
            r.origin.x = min(max(r.minX, 0), max(0, bounds.width - r.width))
            r.origin.y = min(max(r.minY, 0), max(0, bounds.height - r.height))
            setSelection(r)
        case .resize:
            setSelection(Geometry.resize(dragOrigSel, handle: resizeHandle, dx: p.x - dragStart.x, dy: p.y - dragStart.y))
        case .draw:
            updateDraw(p, shift: event.modifierFlags.contains(.shift))
        case .moveText:
            moveEditingText(to: CGPoint(x: p.x - textGrab.x, y: p.y - textGrab.y))
        case .none:
            break
        }
    }

    override func mouseUp(with event: NSEvent) {
        let p = convert(event.locationInWindow, from: nil)
        switch drag {
        case .pending:
            drag = .none
            if let w = windowAt(p) {
                session?.selectionStarted(on: self)
                hoverWindow = nil
                setSelection(w)
                Log.debug("selected window rect \(w)")
            }
        case .draw:
            endDraw()
        default:
            drag = .none
        }
        if let sel = selection, sel.width < 2 || sel.height < 2 { selection = nil }
        updateHover(p)
        needsDisplay = true
    }

    override func rightMouseDown(with event: NSEvent) {
        if drag != .none { cancelDrag() }
        else if editing != nil { commitText() }
        else if selection == nil { session?.close(reason: "right-click") }
    }

    override func mouseMoved(with event: NSEvent) { updateHover(convert(event.locationInWindow, from: nil)) }

    override func mouseExited(with event: NSEvent) {
        previewAt = nil
        needsDisplay = true
    }

    private func updateHover(_ p: CGPoint) {
        if selection == nil {
            let w = windowAt(p)
            if w != hoverWindow { hoverWindow = w; needsDisplay = true }
            NSCursor.crosshair.set()
            return
        }
        let b = hitButton(p)
        if b !== hoverButton { hoverButton = b; needsDisplay = true }
        let showPreview = selection!.contains(p) && drag == .none && editing == nil && previewDiameter != nil && b == nil && !onChrome(p) && hitHandle(p) == nil
        let newPreview: CGPoint? = showPreview ? p : nil
        if newPreview != previewAt { previewAt = newPreview; needsDisplay = true }
        cursor(for: p, b).set()
    }

    private func cursor(for p: CGPoint, _ b: Button?) -> NSCursor {
        if b != nil || onChrome(p) { return .pointingHand }
        if let editing, editing.bounds.contains(p) { return .openHand }
        if let h = hitHandle(p) { return [1, 5].contains(h) ? .resizeUpDown : [3, 7].contains(h) ? .resizeLeftRight : .crosshair }
        if let sel = selection, sel.contains(p) {
            switch tool { case .none: return .openHand; case .text: return .iBeam; default: return .crosshair }
        }
        return .crosshair
    }

    override func scrollWheel(with event: NSEvent) {
        scrollAccum += event.scrollingDeltaY * (event.hasPreciseScrollingDeltas ? 0.1 : 1)
        guard abs(scrollAccum) >= 1 else { return }
        let step = scrollAccum > 0 ? 1 : -1
        scrollAccum = 0
        let at = convert(event.locationInWindow, from: nil)

        if event.modifierFlags.contains(.option) && tool == .redact {
            let steps = Redaction.cellSteps
            let idx = min(max((steps.firstIndex(of: redactCell) ?? 4) + step, 0), steps.count - 1)
            redactCell = steps[idx]
            settings.redactCell = redactCell
            if drag == .draw, let live = current as? RedactMark { live.cell = redactCell }
            showBubble("Cell \(redactCell) px", at)
            return
        }
        if drag == .draw && current is RedactMark { return }
        size = min(max(size + step, 1), 20)
        settings.size = size
        editing?.size = CGFloat(size)
        if drag == .draw { current?.size = CGFloat(size) }
        showBubble("Size \(size)", at)
    }

    private func showBubble(_ text: String, _ at: CGPoint) {
        bubble = (text, at, Date().addingTimeInterval(0.9))
        needsDisplay = true
        DispatchQueue.main.asyncAfter(deadline: .now() + 1.0) { [weak self] in self?.needsDisplay = true }
    }

    // MARK: - Selection and drawing

    private func setSelection(_ r: CGRect) {
        selection = r.intersection(bounds)
        needsDisplay = true
    }

    private func cancelDrag() {
        switch drag {
        case .draw: current = nil
        case .move, .resize: setSelection(dragOrigSel)
        case .newSelection: selection = selectionBeforeDrag
        case .moveText: moveEditingText(to: textOriginBefore)
        default: break
        }
        drag = .none
        needsDisplay = true
    }

    private func beginDraw(_ p: CGPoint) {
        let s = CGFloat(size)
        switch tool {
        case .text:
            let note = TextMark(); note.color = color; note.size = s; note.origin = CGPoint(x: p.x, y: p.y - (8 + s * 3) * 0.6)
            editing = note
            needsDisplay = true
            return
        case .number:
            let m = NumberMark(); m.color = color; m.size = s; m.center = p; m.number = annotations.filter { $0 is NumberMark }.count + 1
            current = m
        case .emoji:
            let m = EmojiMark(); m.size = s; m.center = p; m.emoji = EmojiSet.all[emojiIndex]
            current = m
        case .pen, .highlighter:
            let m = StrokeMark(); m.color = color; m.size = s; m.highlighter = tool == .highlighter; m.points = [p]
            current = m
        case .line, .arrow:
            let m = LineMark(); m.color = color; m.size = s; m.start = p; m.end = p; m.arrow = tool == .arrow
            current = m
        case .rectangle, .ellipse:
            let m = BoxMark(); m.color = color; m.size = s; m.rect = CGRect(origin: p, size: .zero); m.ellipse = tool == .ellipse
            current = m
        case .redact:
            let m = RedactMark(); m.style = redactStyle; m.shape = redactShape; m.secure = redactSecure
            m.brushWidth = RedactMark.brushWidth(for: s); m.cell = redactCell; m.preview = true
            m.rect = CGRect(origin: p, size: .zero)
            if redactShape == .brush { m.points = [p] }
            current = m
        case .none:
            return
        }
        drag = .draw
        needsDisplay = true
    }

    private func updateDraw(_ p: CGPoint, shift: Bool) {
        switch current {
        case let m as StrokeMark:
            if let last = m.points.last, abs(last.x - p.x) < 1 && abs(last.y - p.y) < 1 { return }
            m.points.append(p)
        case let m as LineMark:
            m.end = shift ? Geometry.snap45(from: m.start, to: p) : p
        case let m as BoxMark:
            m.rect = Geometry.box(dragStart, p, square: shift)
        case let m as NumberMark:
            m.center = p
        case let m as EmojiMark:
            let dx = p.x - m.center.x, dy = p.y - m.center.y
            guard dx * dx + dy * dy >= 100 else { return }
            let angle = atan2(dx, -dy) * 180 / .pi
            m.angle = shift ? (angle / 15).rounded() * 15 : angle
        case let m as RedactMark:
            if m.shape == .box {
                m.rect = Geometry.box(dragStart, p, square: shift)
            } else {
                if let last = m.points.last, abs(last.x - p.x) < 2 && abs(last.y - p.y) < 2 { return }
                m.points.append(p)
            }
            m.changed()
        default:
            return
        }
        needsDisplay = true
    }

    private func endDraw() {
        drag = .none
        guard let c = current else { return }
        current = nil
        let keep: Bool
        switch c {
        case let l as LineMark: keep = abs(l.end.x - l.start.x) + abs(l.end.y - l.start.y) >= 2
        case let b as BoxMark: keep = b.rect.width >= 2 && b.rect.height >= 2
        case let r as RedactMark: keep = r.shape == .brush || !r.isEmpty
        default: keep = true
        }
        if let r = c as? RedactMark { r.preview = false }
        if keep { push(c) }
        Log.debug("drew \(type(of: c)) kept=\(keep)")
    }

    private func push(_ a: Annotation) {
        annotations.append(a)
        redoStack.removeAll()
        needsDisplay = true
    }

    private func commitText() {
        guard let note = editing else { return }
        editing = nil
        if !note.text.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty { push(note) }
        needsDisplay = true
    }

    private func moveEditingText(to origin: CGPoint) {
        guard let note = editing else { return }
        var o = origin
        if let sel = selection {
            o.x = min(max(o.x, sel.minX), max(sel.minX, sel.maxX - 4))
            o.y = min(max(o.y, sel.minY), max(sel.minY, sel.maxY - 4))
        }
        note.origin = o
        needsDisplay = true
    }

    private func undoLast() {
        if editing != nil { editing = nil }
        else if let a = annotations.popLast() { redoStack.append(a) }
        needsDisplay = true
    }

    private func redoLast() {
        if let a = redoStack.popLast() { annotations.append(a) }
        needsDisplay = true
    }

    private func setTool(_ t: Tool) {
        commitText()
        tool = t
        previewAt = nil
        Log.debug("tool -> \(t.rawValue)")
        needsDisplay = true
    }

    private func setColor(_ c: NSColor) {
        color = c
        settings.color = c
        editing?.color = c
    }

    private func press(_ b: Button) {
        switch b.kind {
        case .tool: setTool(tool == b.tool ? .none : b.tool)
        case .color: paletteOpen.toggle()
        case .undo: undoLast()
        case .redo: redoLast()
        case .copy: copyToClipboard()
        case .save: saveAs()
        case .close: session?.close(reason: "close button")
        case .redactStyle:
            redactStyle = RedactStyle(rawValue: b.option) ?? .pixelate; settings.redactStyle = redactStyle
        case .redactShape:
            redactShape = RedactShape(rawValue: b.option) ?? .brush; settings.redactShape = redactShape
        case .redactSecure:
            redactSecure.toggle(); settings.redactSecure = redactSecure; updateSecureTip()
            Log.info("redaction secure mode: \(redactSecure)")
        case .emoji:
            emojiIndex = b.option; settings.emojiIndex = emojiIndex
        }
        needsDisplay = true
    }

    // MARK: - Keyboard

    override func keyDown(with event: NSEvent) {
        let cmd = event.modifierFlags.contains(.command)
        let shift = event.modifierFlags.contains(.shift)
        let option = event.modifierFlags.contains(.option)
        let key = event.charactersIgnoringModifiers?.lowercased() ?? ""
        let code = event.keyCode

        // Undo, redo, copy, paste, select all and save arrive as menu actions (see the @objc actions below and
        // AppDelegate.buildMainMenu), so the user's App Shortcuts customisations apply to them.
        if let note = editing {
            if code == 53 || (code == 36 && !shift) { commitText(); return }
            if code == 36 && shift { append("\n"); return }
            if code == 51 {
                if !note.text.isEmpty { note.text = option ? Geometry.trimWord(note.text) : String(note.text.dropLast()) }
                needsDisplay = true
                return
            }
            if !cmd, let chars = event.characters, !chars.isEmpty, chars.unicodeScalars.allSatisfy({ !CharacterSet.controlCharacters.contains($0) }) {
                append(chars)
            }
            return
        }

        if code == 53 {
            if drag != .none { cancelDrag() }
            else if paletteOpen { paletteOpen = false; needsDisplay = true }
            else { session?.close(reason: "escape") }
            return
        }
        if code == 36 && !cmd { copyToClipboard(); return }
        if cmd || option || selection == nil { super.keyDown(with: event); return }

        let map: [String: Tool] = ["p": .pen, "l": .line, "a": .arrow, "r": .rectangle, "e": .ellipse, "h": .highlighter,
                                   "t": .text, "n": .number, "m": .emoji, "b": .redact]
        if let t = map[key] { setTool(tool == t ? .none : t); return }
        if key == "f" && tool == .redact && drag != .draw {
            redactShape = redactShape == .box ? .brush : .box
            settings.redactShape = redactShape
            needsDisplay = true
            return
        }
        if let sel = selection, [123, 124, 125, 126].contains(code) {
            let dx: CGFloat = code == 123 ? -1 : code == 124 ? 1 : 0
            let dy: CGFloat = code == 126 ? -1 : code == 125 ? 1 : 0
            var r = sel
            if shift { r.size.width = max(2, r.width + dx); r.size.height = max(2, r.height + dy) }
            else {
                r.origin.x = min(max(r.minX + dx, 0), max(0, bounds.width - r.width))
                r.origin.y = min(max(r.minY + dy, 0), max(0, bounds.height - r.height))
            }
            setSelection(r)
        }
    }

    // MARK: - Menu actions

    @objc func snipUndo(_ sender: Any?) {
        if let note = editing, !note.text.isEmpty {
            note.text = Geometry.trimWord(note.text)
            needsDisplay = true
        } else {
            undoLast()
        }
    }

    @objc func snipRedo(_ sender: Any?) { redoLast() }

    @objc func snipCopy(_ sender: Any?) {
        commitText()
        copyToClipboard()
    }

    @objc func snipPaste(_ sender: Any?) {
        guard editing != nil, let s = NSPasteboard.general.string(forType: .string) else { return }
        append(String(s.replacingOccurrences(of: "\r\n", with: "\n").prefix(2000)))
    }

    @objc func snipSelectAll(_ sender: Any?) {
        guard editing == nil else { return }
        session?.selectionStarted(on: self)
        setSelection(bounds)
    }

    @objc func snipSave(_ sender: Any?) {
        commitText()
        saveAs()
    }

    func validateMenuItem(_ item: NSMenuItem) -> Bool {
        switch item.action {
        case #selector(snipPaste(_:)): return editing != nil
        case #selector(snipCopy(_:)), #selector(snipSave(_:)): return selection != nil
        case #selector(snipRedo(_:)): return !redoStack.isEmpty
        default: return true
        }
    }

    private func append(_ s: String) {
        guard let note = editing, note.text.count < 5000 else { return }
        note.text += s
        needsDisplay = true
    }

    // MARK: - Output

    /// Flattens the selection and its annotations into one opaque image at the display's pixel resolution.
    func compose() -> CGImage? {
        guard let sel = selection, sel.width >= 1, sel.height >= 1 else { return nil }
        let s = capture.scale
        let w = Int((sel.width * s).rounded()), h = Int((sel.height * s).rounded())
        guard let ctx = CGContext(data: nil, width: w, height: h, bitsPerComponent: 8, bytesPerRow: 0, space: PixelBridge.colorSpace,
                                  bitmapInfo: PixelBridge.bitmapInfo) else { return nil }
        ctx.translateBy(x: 0, y: CGFloat(h))
        ctx.scaleBy(x: s, y: -s)
        ctx.translateBy(x: -sel.minX, y: -sel.minY)
        drawImageUpright(ctx, capture.image, in: bounds)
        ctx.clip(to: sel)
        for a in annotations { a.draw(ctx, env) }
        return ctx.makeImage()
    }

    private func copyToClipboard() {
        let start = DispatchTime.now()
        guard let image = compose() else { return }
        let rep = NSBitmapImageRep(cgImage: image)
        guard let png = rep.representation(using: .png, properties: [:]) else { Log.error("PNG encoding failed"); return }
        let pb = NSPasteboard.general
        pb.clearContents()
        let ok = pb.setData(png, forType: .png)
        pb.setData(rep.tiffRepresentation, forType: .tiff)
        Log.info("copied \(image.width)x\(image.height) to clipboard ok=\(ok) in \(Log.ms(since: start)) ms")
        if !ok { alert("Couldn't copy to the clipboard."); return }
        session?.close(reason: "copied")
    }

    private func saveAs() {
        guard let image = compose() else { return }
        let panel = NSSavePanel()
        panel.title = "Save screenshot"
        panel.allowedContentTypes = [.png, .jpeg]
        let f = DateFormatter(); f.dateFormat = "yyyy-MM-dd HH.mm.ss"
        panel.nameFieldStringValue = "Snip \(f.string(from: Date())).png"
        let dir = settings.lastSaveDirectory
        panel.directoryURL = (dir.map { FileManager.default.fileExists(atPath: $0.path) } ?? false) ? dir
            : FileManager.default.urls(for: .picturesDirectory, in: .userDomainMask).first
        panel.level = NSWindow.Level(rawValue: NSWindow.Level.screenSaver.rawValue + 1)
        guard panel.runModal() == .OK, let url = panel.url else { Log.debug("save cancelled"); return }
        let rep = NSBitmapImageRep(cgImage: image)
        let jpeg = ["jpg", "jpeg"].contains(url.pathExtension.lowercased())
        guard let data = rep.representation(using: jpeg ? .jpeg : .png, properties: jpeg ? [.compressionFactor: 0.95] : [:]) else {
            alert("Couldn't encode the image."); return
        }
        do {
            try data.write(to: url)
            settings.lastSaveDirectory = url.deletingLastPathComponent()
            Log.info("saved \(image.width)x\(image.height) as \(url.pathExtension)")
            session?.close(reason: "saved")
        } catch {
            Log.error("save failed: \(error)")
            alert("Couldn't save \(url.lastPathComponent).\n\n\(error.localizedDescription)")
        }
    }

    private func alert(_ text: String) {
        let a = NSAlert()
        a.messageText = "Snip"
        a.informativeText = text
        a.window.level = NSWindow.Level(rawValue: NSWindow.Level.screenSaver.rawValue + 1)
        a.runModal()
    }
}
