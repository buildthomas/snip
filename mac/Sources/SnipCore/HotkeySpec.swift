import Foundation

/// A global hotkey: one key plus modifiers. Parses and prints specs like "cmd+shift+s" and shows "⇧⌘S".
public struct HotkeySpec: Equatable {
    public var keyCode: UInt32
    public var command: Bool
    public var shift: Bool
    public var option: Bool
    public var control: Bool
    /// Display name of the key ("S", "F5", "Space", or whatever the keyboard produced for keys not in the table).
    public var keyLabel: String

    // Carbon modifier masks (cmdKey, shiftKey, optionKey, controlKey).
    public static let carbonCommand: UInt32 = 1 << 8
    public static let carbonShift: UInt32 = 1 << 9
    public static let carbonOption: UInt32 = 1 << 11
    public static let carbonControl: UInt32 = 1 << 12

    public static let defaultSpec = HotkeySpec(keyCode: 0x13, command: true, shift: true, option: false, control: false)
    /// Recording: the default capture hotkey plus Option (⌥⇧⌘2).
    public static let defaultRecordSpec = HotkeySpec(keyCode: 0x13, command: true, shift: true, option: true, control: false)

    public init(keyCode: UInt32, command: Bool, shift: Bool, option: Bool, control: Bool, keyLabel: String? = nil) {
        self.keyCode = keyCode
        self.command = command
        self.shift = shift
        self.option = option
        self.control = control
        self.keyLabel = keyLabel ?? Self.label(for: keyCode) ?? "Key \(keyCode)"
    }

    public init(keyCode: UInt32, carbonModifiers m: UInt32, keyLabel: String? = nil) {
        self.init(keyCode: keyCode, command: m & Self.carbonCommand != 0, shift: m & Self.carbonShift != 0,
                  option: m & Self.carbonOption != 0, control: m & Self.carbonControl != 0, keyLabel: keyLabel)
    }

    public var carbonModifiers: UInt32 {
        (command ? Self.carbonCommand : 0) | (shift ? Self.carbonShift : 0) | (option ? Self.carbonOption : 0) | (control ? Self.carbonControl : 0)
    }

    /// Shift or no modifier at all would swallow normal typing, so a hotkey needs Command, Option or Control.
    public var hasRequiredModifier: Bool { command || option || control }

    /// Mac menu order: ⌃⌥⇧⌘ then the key.
    public var display: String {
        (control ? "⌃" : "") + (option ? "⌥" : "") + (shift ? "⇧" : "") + (command ? "⌘" : "") + keyLabel
    }

    /// Text form accepted by `parse`, e.g. "ctrl+shift+cmd+s".
    public var spec: String {
        var parts: [String] = []
        if control { parts.append("ctrl") }
        if option { parts.append("opt") }
        if shift { parts.append("shift") }
        if command { parts.append("cmd") }
        parts.append(Self.name(for: keyCode) ?? keyLabel.lowercased())
        return parts.joined(separator: "+")
    }

    /// Well-known app shortcuts; taking one over globally hides it from every app.
    public var commonlyUsedByApps: Bool {
        guard command, !option, !control else { return false }
        let plain: Set<String> = ["a", "c", "f", "n", "o", "p", "q", "s", "t", "v", "w", "x", "z"]
        let shifted: Set<String> = ["n", "s", "t", "z"]
        let key = Self.name(for: keyCode) ?? ""
        return shift ? shifted.contains(key) : plain.contains(key)
    }

    /// Parses "cmd+shift+s", "Shift+Command+S", "ctrl-opt-f5" and similar. Returns nil for unknown keys or no key.
    public static func parse(_ text: String) -> HotkeySpec? {
        let tokens = text.lowercased().split(whereSeparator: { $0 == "+" || $0 == "-" || $0 == " " }).map(String.init)
        var cmd = false, shift = false, opt = false, ctrl = false
        var key: UInt32?
        for t in tokens {
            switch t {
            case "cmd", "command", "⌘", "super", "meta": cmd = true
            case "shift", "⇧": shift = true
            case "opt", "option", "alt", "⌥": opt = true
            case "ctrl", "control", "⌃": ctrl = true
            default:
                guard key == nil, let code = keyCodes[t] else { return nil }
                key = code
            }
        }
        guard let key else { return nil }
        return HotkeySpec(keyCode: key, command: cmd, shift: shift, option: opt, control: ctrl)
    }

    /// Key codes of the physical keys (ANSI positions), by name.
    public static let keyCodes: [String: UInt32] = {
        var m: [String: UInt32] = [
            "a": 0x00, "s": 0x01, "d": 0x02, "f": 0x03, "h": 0x04, "g": 0x05, "z": 0x06, "x": 0x07, "c": 0x08, "v": 0x09,
            "b": 0x0B, "q": 0x0C, "w": 0x0D, "e": 0x0E, "r": 0x0F, "y": 0x10, "t": 0x11, "o": 0x1F, "u": 0x20, "i": 0x22,
            "p": 0x23, "l": 0x25, "j": 0x26, "k": 0x28, "n": 0x2D, "m": 0x2E,
            "1": 0x12, "2": 0x13, "3": 0x14, "4": 0x15, "5": 0x17, "6": 0x16, "7": 0x1A, "8": 0x1C, "9": 0x19, "0": 0x1D,
            "space": 0x31,
            "f1": 0x7A, "f2": 0x78, "f3": 0x63, "f4": 0x76, "f5": 0x60, "f6": 0x61, "f7": 0x62, "f8": 0x64,
            "f9": 0x65, "f10": 0x6D, "f11": 0x67, "f12": 0x6F,
        ]
        m["spacebar"] = 0x31
        return m
    }()

    public static func name(for keyCode: UInt32) -> String? {
        keyCodes.filter { $0.key != "spacebar" }.first { $0.value == keyCode }?.key
    }

    public static func label(for keyCode: UInt32) -> String? {
        guard let n = name(for: keyCode) else { return nil }
        return n == "space" ? "Space" : n.uppercased()
    }
}
