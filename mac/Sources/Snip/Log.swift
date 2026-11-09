import Foundation
import os

/// File log at ~/Library/Logs/Snip/snip.log (also mirrored to the unified log, subsystem com.buildthomas.snip).
/// Records lifecycle, timings and errors. Never logs typed text, key presses beyond hotkey/command names, or pixels.
enum Log {
    private static let queue = DispatchQueue(label: "snip.log")
    private static let osLog = Logger(subsystem: "com.buildthomas.snip", category: "app")
    private static let maxBytes = 1_000_000

    static var directory: URL { FileManager.default.homeDirectoryForCurrentUser.appendingPathComponent("Library/Logs/Snip") }
    static var file: URL { directory.appendingPathComponent("snip.log") }

    /// Verbose debug logging; on by default while the macOS port is experimental.
    static var debugEnabled: Bool {
        if ProcessInfo.processInfo.environment["SNIP_DEBUG"] == "1" { return true }
        return Settings.shared.debugLogging
    }

    static func info(_ message: String) { write("INFO ", message) }
    /// Waits for pending writes (before an early exit).
    static func flush() { queue.sync {} }
    static func error(_ message: String) { write("ERROR", message) }
    static func debug(_ message: @autoclosure () -> String) {
        guard debugEnabled else { return }
        write("DEBUG", message())
    }

    private static func write(_ level: String, _ message: String) {
        let line = "\(timestamp()) \(level) \(message)\n"
        osLog.log("\(level, privacy: .public) \(message, privacy: .public)")
        if ProcessInfo.processInfo.environment["SNIP_LOG_STDERR"] == "1" { FileHandle.standardError.write(Data(line.utf8)) }
        queue.async {
            let fm = FileManager.default
            try? fm.createDirectory(at: directory, withIntermediateDirectories: true)
            if let size = (try? fm.attributesOfItem(atPath: file.path))?[.size] as? Int, size > maxBytes {
                let old = directory.appendingPathComponent("snip.1.log")
                try? fm.removeItem(at: old)
                try? fm.moveItem(at: file, to: old)
            }
            if !fm.fileExists(atPath: file.path) { fm.createFile(atPath: file.path, contents: nil) }
            if let h = try? FileHandle(forWritingTo: file) {
                h.seekToEndOfFile()
                h.write(Data(line.utf8))
                try? h.close()
            }
        }
    }

    private static func timestamp() -> String {
        let f = DateFormatter()
        f.dateFormat = "yyyy-MM-dd HH:mm:ss.SSS"
        return f.string(from: Date())
    }

    /// Times a block and logs it at debug level.
    static func time<T>(_ label: String, _ body: () throws -> T) rethrows -> T {
        let start = DispatchTime.now()
        defer { debug("\(label): \(ms(since: start)) ms") }
        return try body()
    }

    static func ms(since start: DispatchTime) -> String {
        String(format: "%.1f", Double(DispatchTime.now().uptimeNanoseconds - start.uptimeNanoseconds) / 1_000_000)
    }
}
