import AppKit
import SnipCore

/// Everything a shape needs to draw: the frozen screen image and its pixels-per-point scale.
struct RenderEnv {
    let source: CGImage
    let scale: CGFloat
}

/// Geometry is in the screen's points, origin top-left, y down. `bounds` must cover everything drawn, at any rotation.
class Annotation {
    var color: NSColor = .systemRed
    var size: CGFloat = 4
    func draw(_ ctx: CGContext, _ env: RenderEnv) {}
    var bounds: CGRect { .null }
}

final class StrokeMark: Annotation {
    var points: [CGPoint] = []
    var highlighter = false
    static let highlighterAlpha: CGFloat = 0.43
    var width: CGFloat { highlighter ? size * 4 : size }

    override func draw(_ ctx: CGContext, _ env: RenderEnv) {
        guard let first = points.first else { return }
        let c = highlighter ? color.withAlphaComponent(Self.highlighterAlpha) : color
        ctx.saveGState()
        ctx.setStrokeColor(c.cgColor)
        ctx.setFillColor(c.cgColor)
        if points.count == 1 {
            ctx.fillEllipse(in: CGRect(x: first.x - width / 2, y: first.y - width / 2, width: width, height: width))
        } else {
            ctx.setLineWidth(width)
            ctx.setLineCap(.round)
            ctx.setLineJoin(.round)
            ctx.addLines(between: points)
            ctx.strokePath()
        }
        ctx.restoreGState()
    }

    override var bounds: CGRect {
        guard !points.isEmpty else { return .null }
        let xs = points.map(\.x), ys = points.map(\.y)
        return CGRect(x: xs.min()!, y: ys.min()!, width: xs.max()! - xs.min()!, height: ys.max()! - ys.min()!).insetBy(dx: -width / 2 - 2, dy: -width / 2 - 2)
    }
}

final class LineMark: Annotation {
    var start: CGPoint = .zero
    var end: CGPoint = .zero
    var arrow = false
    var headLength: CGFloat { max(12, size * 4) }

    override func draw(_ ctx: CGContext, _ env: RenderEnv) {
        ctx.saveGState()
        ctx.setStrokeColor(color.cgColor)
        ctx.setFillColor(color.cgColor)
        ctx.setLineWidth(size)
        ctx.setLineCap(.round)
        let dx = end.x - start.x, dy = end.y - start.y, len = hypot(dx, dy)
        if !arrow || len < 1 {
            ctx.move(to: start); ctx.addLine(to: end); ctx.strokePath()
        } else {
            let head = min(headLength, len), ux = dx / len, uy = dy / len
            let base = CGPoint(x: end.x - ux * head, y: end.y - uy * head), half = head * 0.55
            ctx.setLineCap(.butt)
            ctx.move(to: start); ctx.addLine(to: CGPoint(x: base.x + ux, y: base.y + uy)); ctx.strokePath()
            ctx.move(to: end)
            ctx.addLine(to: CGPoint(x: base.x - uy * half, y: base.y + ux * half))
            ctx.addLine(to: CGPoint(x: base.x + uy * half, y: base.y - ux * half))
            ctx.closePath(); ctx.fillPath()
        }
        ctx.restoreGState()
    }

    override var bounds: CGRect {
        CGRect(x: min(start.x, end.x), y: min(start.y, end.y), width: abs(end.x - start.x), height: abs(end.y - start.y))
            .insetBy(dx: -(headLength + size + 2), dy: -(headLength + size + 2))
    }
}

final class BoxMark: Annotation {
    var rect: CGRect = .zero
    var ellipse = false

    override func draw(_ ctx: CGContext, _ env: RenderEnv) {
        ctx.saveGState()
        ctx.setStrokeColor(color.cgColor)
        ctx.setLineWidth(size)
        if ellipse { ctx.strokeEllipse(in: rect) } else { ctx.stroke(rect) }
        ctx.restoreGState()
    }

    override var bounds: CGRect { rect.insetBy(dx: -size - 2, dy: -size - 2) }
}

final class TextMark: Annotation {
    var origin: CGPoint = .zero
    var text = ""
    var fontSize: CGFloat { 8 + size * 3 }
    var attributes: [NSAttributedString.Key: Any] {
        [.font: NSFont.systemFont(ofSize: fontSize, weight: .semibold), .foregroundColor: color]
    }

    override func draw(_ ctx: CGContext, _ env: RenderEnv) {
        guard !text.isEmpty else { return }
        withFlippedNSContext(ctx) { (text as NSString).draw(at: origin, withAttributes: attributes) }
    }

    func textSize() -> CGSize {
        let s = (text.isEmpty ? " " : text + (text.hasSuffix("\n") ? " " : "")) as NSString
        let r = s.size(withAttributes: attributes)
        return CGSize(width: max(r.width, 2), height: max(r.height, fontSize * 1.2))
    }

    override var bounds: CGRect { CGRect(origin: origin, size: textSize()).insetBy(dx: -6, dy: -6) }
}

final class NumberMark: Annotation {
    var center: CGPoint = .zero
    var number = 1
    var radius: CGFloat { 10 + size * 2 }

    override func draw(_ ctx: CGContext, _ env: RenderEnv) {
        let r = radius, rect = CGRect(x: center.x - r, y: center.y - r, width: r * 2, height: r * 2)
        let fg: NSColor = color.luminance > 0.67 ? .black : .white
        ctx.saveGState()
        ctx.setFillColor(color.cgColor)
        ctx.fillEllipse(in: rect)
        ctx.setStrokeColor(fg.withAlphaComponent(color.alphaComponent).cgColor)
        ctx.setLineWidth(max(1.5, r / 10))
        ctx.strokeEllipse(in: rect)
        ctx.restoreGState()
        let attrs: [NSAttributedString.Key: Any] = [
            .font: NSFont.systemFont(ofSize: r * (number >= 10 ? 0.95 : 1.15), weight: .bold),
            .foregroundColor: fg.withAlphaComponent(color.alphaComponent),
        ]
        let s = "\(number)" as NSString
        let sz = s.size(withAttributes: attrs)
        withFlippedNSContext(ctx) { s.draw(at: CGPoint(x: center.x - sz.width / 2, y: center.y - sz.height / 2), withAttributes: attrs) }
    }

    override var bounds: CGRect { CGRect(x: center.x - radius - 3, y: center.y - radius - 3, width: radius * 2 + 6, height: radius * 2 + 6) }
}

final class EmojiMark: Annotation {
    var emoji = "😀"
    var center: CGPoint = .zero
    var angle: CGFloat = 0
    var opacity: CGFloat = 1
    static func points(for size: CGFloat) -> CGFloat { 16 + size * 6 }

    override func draw(_ ctx: CGContext, _ env: RenderEnv) {
        let px = Self.points(for: size)
        let attrs: [NSAttributedString.Key: Any] = [.font: NSFont(name: "Apple Color Emoji", size: px * 0.8) ?? NSFont.systemFont(ofSize: px * 0.8)]
        let s = emoji as NSString
        let sz = s.size(withAttributes: attrs)
        ctx.saveGState()
        ctx.setAlpha(opacity)
        ctx.translateBy(x: center.x, y: center.y)
        ctx.rotate(by: angle * .pi / 180)
        withFlippedNSContext(ctx) { s.draw(at: CGPoint(x: -sz.width / 2, y: -sz.height / 2), withAttributes: attrs) }
        ctx.restoreGState()
    }

    override var bounds: CGRect {
        let r = Self.points(for: size) * 0.8 + 2
        return CGRect(x: center.x - r, y: center.y - r, width: r * 2, height: r * 2)
    }
}

/// Black is a solid fill. Secure pixelate/blur come from SnipCore's generated rendering (nothing underneath is
/// recoverable); insecure ones are a real pixelation/blur of the covered pixels.
final class RedactMark: Annotation {
    var style: RedactStyle = .pixelate
    var shape: RedactShape = .brush
    var secure = true
    var seed: UInt64 = Redaction.newSeed()
    var rect: CGRect = .zero
    var points: [CGPoint] = []
    var brushWidth: CGFloat = 24
    /// Cell size / blur radius in pixels.
    var cell = 12 { didSet { changed() } }
    /// Cheaper rendering while dragging (no edge blending).
    var preview = false { didSet { if preview != oldValue { changed() } } }

    private var version = 0
    private var tile: CGImage?
    private var tileRect = IntRect(x: 0, y: 0, width: 0, height: 0)
    private var tileVersion = -1

    static func brushWidth(for size: CGFloat) -> CGFloat { 8 + size * 4 }
    func changed() { version += 1 }

    var path: CGPath {
        switch shape {
        case .box:
            return CGPath(rect: rect, transform: nil)
        case .brush:
            guard let first = points.first else { return CGMutablePath() }
            if points.count == 1 {
                return CGPath(ellipseIn: CGRect(x: first.x - brushWidth / 2, y: first.y - brushWidth / 2, width: brushWidth, height: brushWidth), transform: nil)
            }
            let line = CGMutablePath()
            line.addLines(between: points)
            return line.copy(strokingWithWidth: brushWidth, lineCap: .round, lineJoin: .round, miterLimit: 10)
        }
    }

    var isEmpty: Bool {
        let b = path.boundingBoxOfPath
        return path.isEmpty || b.width < 2 || b.height < 2
    }

    override var bounds: CGRect { path.isEmpty ? .null : path.boundingBoxOfPath.insetBy(dx: -2, dy: -2) }

    override func draw(_ ctx: CGContext, _ env: RenderEnv) {
        let p = path
        guard !p.isEmpty else { return }
        ctx.saveGState()
        ctx.setShouldAntialias(false)
        ctx.addPath(p)
        if style == .black {
            ctx.setFillColor(NSColor.black.cgColor)
            ctx.fillPath(using: .winding)
        } else {
            ctx.clip(using: .winding)
            if let tile = makeTile(for: p, env) {
                let s = env.scale
                drawImageUpright(ctx, tile, in: CGRect(x: CGFloat(tileRect.x) / s, y: CGFloat(tileRect.y) / s,
                                                       width: CGFloat(tileRect.width) / s, height: CGFloat(tileRect.height) / s))
            }
        }
        ctx.restoreGState()
    }

    private func makeTile(for p: CGPath, _ env: RenderEnv) -> CGImage? {
        let s = env.scale, bb = p.boundingBoxOfPath
        let b = IntRect(x: Int((bb.minX * s).rounded(.down)), y: Int((bb.minY * s).rounded(.down)),
                        width: Int((bb.maxX * s).rounded(.up)) - Int((bb.minX * s).rounded(.down)),
                        height: Int((bb.maxY * s).rounded(.up)) - Int((bb.minY * s).rounded(.down)))
            .intersection(IntRect(x: 0, y: 0, width: env.source.width, height: env.source.height))
        guard !b.isEmpty else { return nil }
        if let tile, tileVersion == version, tileRect == b { return tile }

        let start = DispatchTime.now()
        let out: PixelBuffer
        if !secure {
            let e = b.insetBy(cell * 3).intersection(IntRect(x: 0, y: 0, width: env.source.width, height: env.source.height))
            let src = PixelBridge.read(env.source, e)
            out = style == .pixelate
                ? Redaction.realPixelate(source: src, sourceRect: e, target: b, cell: cell)
                : Redaction.realBlur(source: src, sourceRect: e, target: b, radius: cell)
        } else {
            let reach = Int(2.5 * Double(cell))
            let e = b.insetBy(reach + 4).intersection(IntRect(x: 0, y: 0, width: env.source.width, height: env.source.height))
            let src = PixelBridge.read(env.source, e)
            let inside = PixelBridge.mask(p, rect: e, scale: s)
            out = Redaction.secure(source: src, sourceRect: e, inside: inside, target: b, seed: seed, cell: cell,
                                   smooth: style == .blur, edges: !preview,
                                   edgeReach: shape == .brush ? Int(brushWidth * s * 0.3) : nil,
                                   edgeStrength: shape == .brush ? 0.45 : 0.7)
        }
        tile = PixelBridge.image(out)
        tileRect = b
        tileVersion = version
        Log.debug("redact tile \(b.width)x\(b.height) px style=\(style) secure=\(secure) cell=\(cell) preview=\(preview): \(Log.ms(since: start)) ms")
        return tile
    }
}

/// Draws a CGImage upright into a y-down context.
func drawImageUpright(_ ctx: CGContext, _ image: CGImage, in rect: CGRect) {
    ctx.saveGState()
    ctx.translateBy(x: rect.minX, y: rect.maxY)
    ctx.scaleBy(x: 1, y: -1)
    ctx.draw(image, in: CGRect(origin: .zero, size: rect.size))
    ctx.restoreGState()
}

/// Runs AppKit text drawing against `ctx` (y-down).
func withFlippedNSContext(_ ctx: CGContext, _ body: () -> Void) {
    NSGraphicsContext.saveGraphicsState()
    NSGraphicsContext.current = NSGraphicsContext(cgContext: ctx, flipped: true)
    body()
    NSGraphicsContext.restoreGraphicsState()
}
