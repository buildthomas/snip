import AppKit
import SnipCore

enum Tool: String, CaseIterable {
    case none, pen, line, arrow, rectangle, ellipse, highlighter, text, number, emoji, redact
}

enum RedactStyle: Int { case black = 0, pixelate = 1, blur = 2 }
enum RedactShape: Int { case box = 0, brush = 2 }

/// Global hotkey choices. macOS reserves Cmd+Shift+3/4/5 for its own screenshots; to use 4 or 5, turn the system
/// shortcut off first (System Settings > Keyboard > Keyboard Shortcuts > Screenshots).
enum HotkeyChoice: String, CaseIterable {
    case cmdShift2 = "cmd-shift-2", cmdShift4 = "cmd-shift-4", cmdShift5 = "cmd-shift-5"
    var title: String {
        switch self {
        case .cmdShift2: return "⌘⇧2"
        case .cmdShift4: return "⌘⇧4"
        case .cmdShift5: return "⌘⇧5"
        }
    }

    /// Carbon virtual key codes for 2, 4 and 5.
    var keyCode: UInt32 {
        switch self {
        case .cmdShift2: return 0x13
        case .cmdShift4: return 0x15
        case .cmdShift5: return 0x17
        }
    }
}

/// UserDefaults-backed settings (domain com.buildthomas.snip). Inspect with `defaults read com.buildthomas.snip`.
final class Settings {
    static let shared = Settings()
    private let d = UserDefaults.standard

    private init() {
        d.register(defaults: [
            "colorHex": "E62828", "size": 4, "redactStyle": RedactStyle.pixelate.rawValue, "redactShape": RedactShape.brush.rawValue,
            "redactCell": 12, "redactSecure": true, "emojiIndex": 0, "hotkey": HotkeyChoice.cmdShift2.rawValue,
            "debugLogging": true, "loginItemInitialized": false,
        ])
    }

    var color: NSColor {
        get { NSColor(hex: d.string(forKey: "colorHex") ?? "E62828") }
        set { d.set(newValue.hex, forKey: "colorHex") }
    }
    var size: Int { get { min(max(d.integer(forKey: "size"), 1), 20) } set { d.set(newValue, forKey: "size") } }
    var redactStyle: RedactStyle {
        get { RedactStyle(rawValue: d.integer(forKey: "redactStyle")) ?? .pixelate }
        set { d.set(newValue.rawValue, forKey: "redactStyle") }
    }
    var redactShape: RedactShape {
        get { RedactShape(rawValue: d.integer(forKey: "redactShape")) ?? .brush }
        set { d.set(newValue.rawValue, forKey: "redactShape") }
    }
    var redactCell: Int {
        get { let v = d.integer(forKey: "redactCell"); return Redaction.cellSteps.contains(v) ? v : 12 }
        set { d.set(newValue, forKey: "redactCell") }
    }
    var redactSecure: Bool { get { d.bool(forKey: "redactSecure") } set { d.set(newValue, forKey: "redactSecure") } }
    var emojiIndex: Int { get { min(max(d.integer(forKey: "emojiIndex"), 0), EmojiSet.all.count - 1) } set { d.set(newValue, forKey: "emojiIndex") } }
    var hotkey: HotkeyChoice {
        get { HotkeyChoice(rawValue: d.string(forKey: "hotkey") ?? "") ?? .cmdShift2 }
        set { d.set(newValue.rawValue, forKey: "hotkey") }
    }
    var lastSaveDirectory: URL? {
        get { d.string(forKey: "lastSaveDirectory").map { URL(fileURLWithPath: $0) } }
        set { d.set(newValue?.path, forKey: "lastSaveDirectory") }
    }
    var debugLogging: Bool { get { d.bool(forKey: "debugLogging") } set { d.set(newValue, forKey: "debugLogging") } }
    var loginItemInitialized: Bool { get { d.bool(forKey: "loginItemInitialized") } set { d.set(newValue, forKey: "loginItemInitialized") } }
}

enum EmojiSet {
    static let all = ["😀", "😂", "😍", "😎", "🤔", "😮", "😢", "😡",
                      "👍", "👎", "👏", "🙏", "❤️", "🔥", "⭐", "💯",
                      "✅", "❌", "⚠️", "❓", "💡", "👀", "🎉", "🚀"]
}

enum Palette {
    static let colors: [NSColor] = [
        NSColor(hex: "E62828"), NSColor(hex: "FF8C00"), NSColor(hex: "FFD600"), NSColor(hex: "28BE46"),
        NSColor(hex: "00BEDC"), NSColor(hex: "1E6EFF"), NSColor(hex: "9646E6"), NSColor(hex: "F046AA"),
        .white, NSColor(hex: "808080"), NSColor(hex: "282828"), .black,
    ]
}

extension NSColor {
    convenience init(hex: String) {
        let v = UInt32(hex, radix: 16) ?? 0xE62828
        self.init(srgbRed: CGFloat((v >> 16) & 255) / 255, green: CGFloat((v >> 8) & 255) / 255, blue: CGFloat(v & 255) / 255, alpha: 1)
    }

    var hex: String {
        let c = usingColorSpace(.sRGB) ?? self
        return String(format: "%02X%02X%02X", Int(c.redComponent * 255), Int(c.greenComponent * 255), Int(c.blueComponent * 255))
    }

    var luminance: CGFloat {
        let c = usingColorSpace(.sRGB) ?? self
        return c.redComponent * 0.299 + c.greenComponent * 0.587 + c.blueComponent * 0.114
    }
}
