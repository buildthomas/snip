import Foundation
import CoreGraphics

public enum Geometry {
    /// Rectangle spanned by two drag points (inclusive of the end pixel).
    public static func normalize(_ a: CGPoint, _ b: CGPoint) -> CGRect {
        CGRect(x: min(a.x, b.x), y: min(a.y, b.y), width: abs(b.x - a.x), height: abs(b.y - a.y))
    }

    /// Handles: 0 TL, 1 T, 2 TR, 3 R, 4 BR, 5 B, 6 BL, 7 L. Flips past the opposite edge.
    public static func resize(_ r: CGRect, handle: Int, dx: CGFloat, dy: CGFloat) -> CGRect {
        var l = r.minX, t = r.minY, rt = r.maxX, b = r.maxY
        if [0, 6, 7].contains(handle) { l += dx }
        if [2, 3, 4].contains(handle) { rt += dx }
        if [0, 1, 2].contains(handle) { t += dy }
        if [4, 5, 6].contains(handle) { b += dy }
        return CGRect(x: min(l, rt), y: min(t, b), width: abs(rt - l), height: abs(b - t))
    }

    public static func handlePoints(_ r: CGRect) -> [CGPoint] {
        [CGPoint(x: r.minX, y: r.minY), CGPoint(x: r.midX, y: r.minY), CGPoint(x: r.maxX, y: r.minY), CGPoint(x: r.maxX, y: r.midY),
         CGPoint(x: r.maxX, y: r.maxY), CGPoint(x: r.midX, y: r.maxY), CGPoint(x: r.minX, y: r.maxY), CGPoint(x: r.minX, y: r.midY)]
    }

    public static func snap45(from: CGPoint, to: CGPoint) -> CGPoint {
        let dx = to.x - from.x, dy = to.y - from.y
        let angle = (atan2(dy, dx) / (.pi / 4)).rounded() * (.pi / 4)
        let len = hypot(dx, dy)
        return CGPoint(x: from.x + cos(angle) * len, y: from.y + sin(angle) * len)
    }

    /// Box from a drag; `square` keeps width == height (Shift).
    public static func box(_ a: CGPoint, _ b: CGPoint, square: Bool) -> CGRect {
        var w = b.x - a.x, h = b.y - a.y
        if square {
            let s = max(abs(w), abs(h))
            w = (w < 0 ? -1 : 1) * s
            h = (h < 0 ? -1 : 1) * s
        }
        return CGRect(x: min(a.x, a.x + w), y: min(a.y, a.y + h), width: abs(w), height: abs(h))
    }

    /// Removes the last word (and trailing spaces), like Ctrl/Option+Backspace.
    public static func trimWord(_ s: String) -> String {
        var chars = Array(s)
        while let last = chars.last, last.isWhitespace { chars.removeLast() }
        while let last = chars.last, !last.isWhitespace { chars.removeLast() }
        return String(chars)
    }
}
