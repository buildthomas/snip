/// Esc closes right away when nothing is drawn; with annotations it needs a second press within the window.
public struct EscapeGuard {
    public static let windowMs: Int64 = 1500
    private var armedAt: Int64?

    public init() {}

    public mutating func shouldClose(hasAnnotations: Bool, nowMs: Int64) -> Bool {
        if !hasAnnotations { armedAt = nil; return true }
        if let t = armedAt, nowMs - t <= Self.windowMs { armedAt = nil; return true }
        armedAt = nowMs
        return false
    }
}
