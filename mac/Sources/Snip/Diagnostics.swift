import AppKit
import ServiceManagement

/// Plain-text environment report for debugging without access to the machine: `Snip --diagnostics`.
enum Diagnostics {
    static func report() -> String {
        var lines = ["--- Snip diagnostics ---"]
        let info = ProcessInfo.processInfo
        lines.append("version: \(snipVersion)")
        lines.append("macOS: \(info.operatingSystemVersionString)")
        lines.append("arch: \(arch())")
        lines.append("bundle path: \(Bundle.main.bundlePath)")
        lines.append("bundle id: \(Bundle.main.bundleIdentifier ?? "(none: not running from an .app bundle)")")
        lines.append("screen recording permission: \(Capture.hasPermission ? "granted" : "NOT granted")")
        lines.append("hotkey: \(Settings.shared.hotkey.display) (\(Settings.shared.hotkey.spec))")
        lines.append("start at login: \(loginStatus())")
        lines.append("debug logging: \(Log.debugEnabled)")
        lines.append("log file: \(Log.file.path)")
        for (i, s) in NSScreen.screens.enumerated() {
            lines.append("screen \(i): '\(s.localizedName)' id=\(Capture.displayID(of: s).map(String.init) ?? "?") frame=\(s.frame) scale=\(s.backingScaleFactor)")
        }
        lines.append("--- end diagnostics ---")
        return lines.joined(separator: "\n")
    }

    private static func loginStatus() -> String {
        switch SMAppService.mainApp.status {
        case .enabled: return "enabled"
        case .notRegistered: return "not registered"
        case .requiresApproval: return "requires approval (System Settings > General > Login Items)"
        case .notFound: return "not found (not running from an installed .app?)"
        @unknown default: return "unknown"
        }
    }

    private static func arch() -> String {
        var sysinfo = utsname()
        uname(&sysinfo)
        return withUnsafeBytes(of: &sysinfo.machine) { raw in
            String(decoding: raw.prefix { $0 != 0 }, as: UTF8.self)
        }
    }
}
