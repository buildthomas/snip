import AppKit
import SnipCore

enum Tool: String, CaseIterable {
    case none, pen, line, arrow, rectangle, ellipse, highlighter, text, number, emoji, redact
}

enum RedactStyle: Int { case black = 0, pixelate = 1, blur = 2 }
enum RedactShape: Int { case box = 0, brush = 2 }

/// UserDefaults-backed settings (domain com.buildthomas.snip). Inspect with `defaults read com.buildthomas.snip`.
final class Settings {
    static let shared = Settings()
    private let d = UserDefaults.standard

    private init() {
        d.register(defaults: [
            "colorHex": "E62828", "size": 4, "redactStyle": RedactStyle.pixelate.rawValue, "redactShape": RedactShape.brush.rawValue,
            "redactCell": 12, "redactSecure": true, "emojiIndex": 0,
            "debugLogging": true, "loginItemInitialized": false, "recordSystemSound": false, "recordMicrophone": false,
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
    /// The global hotkey. Earlier builds stored a preset name under "hotkey"; that is still honoured.
    var hotkey: HotkeySpec {
        get {
            if d.object(forKey: "hotkeyKeyCode") != nil {
                return HotkeySpec(keyCode: UInt32(d.integer(forKey: "hotkeyKeyCode")),
                                  carbonModifiers: UInt32(d.integer(forKey: "hotkeyModifiers")),
                                  keyLabel: d.string(forKey: "hotkeyLabel"))
            }
            switch d.string(forKey: "hotkey") {
            case "cmd-shift-4": return HotkeySpec.parse("cmd+shift+4") ?? .defaultSpec
            case "cmd-shift-5": return HotkeySpec.parse("cmd+shift+5") ?? .defaultSpec
            default: return .defaultSpec
            }
        }
        set {
            d.set(Int(newValue.keyCode), forKey: "hotkeyKeyCode")
            d.set(Int(newValue.carbonModifiers), forKey: "hotkeyModifiers")
            d.set(newValue.keyLabel, forKey: "hotkeyLabel")
            d.removeObject(forKey: "hotkey")
        }
    }
    /// The record hotkey; same storage scheme as `hotkey`, under "record…" keys.
    var recordHotkey: HotkeySpec {
        get {
            guard d.object(forKey: "recordHotkeyKeyCode") != nil else { return .defaultRecordSpec }
            return HotkeySpec(keyCode: UInt32(d.integer(forKey: "recordHotkeyKeyCode")),
                              carbonModifiers: UInt32(d.integer(forKey: "recordHotkeyModifiers")),
                              keyLabel: d.string(forKey: "recordHotkeyLabel"))
        }
        set {
            d.set(Int(newValue.keyCode), forKey: "recordHotkeyKeyCode")
            d.set(Int(newValue.carbonModifiers), forKey: "recordHotkeyModifiers")
            d.set(newValue.keyLabel, forKey: "recordHotkeyLabel")
        }
    }
    var recordSystemSound: Bool { get { d.bool(forKey: "recordSystemSound") } set { d.set(newValue, forKey: "recordSystemSound") } }
    var recordMicrophone: Bool { get { d.bool(forKey: "recordMicrophone") } set { d.set(newValue, forKey: "recordMicrophone") } }
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
