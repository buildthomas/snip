import AppKit
import ScreenCaptureKit

/// One captured display: the frozen image (pixels) and the screen it belongs to.
struct DisplayCapture {
    let screen: NSScreen
    let image: CGImage
    /// Pixels per point.
    var scale: CGFloat { CGFloat(image.width) / screen.frame.width }
}

enum Capture {
    static func displayID(of screen: NSScreen) -> CGDirectDisplayID? {
        screen.deviceDescription[NSDeviceDescriptionKey("NSScreenNumber")] as? CGDirectDisplayID
    }

    static var hasPermission: Bool { CGPreflightScreenCaptureAccess() }

    /// Asks for Screen Recording permission (macOS shows its prompt / settings pane once).
    static func requestPermission() -> Bool {
        let granted = CGRequestScreenCaptureAccess()
        Log.info("screen recording permission requested, granted=\(granted)")
        return granted
    }

    /// Captures every display before any Snip window appears, so open menus and hover states are kept.
    @MainActor
    static func captureAll() async throws -> [DisplayCapture] {
        let start = DispatchTime.now()
        let content = try await SCShareableContent.excludingDesktopWindows(false, onScreenWindowsOnly: true)
        Log.debug("shareable content: \(content.displays.count) displays, \(content.windows.count) windows (\(Log.ms(since: start)) ms)")
        var result: [DisplayCapture] = []
        for screen in NSScreen.screens {
            guard let id = displayID(of: screen), let display = content.displays.first(where: { $0.displayID == id }) else {
                Log.error("no SCDisplay for screen \(screen.localizedName)")
                continue
            }
            let filter = SCContentFilter(display: display, excludingWindows: [])
            let config = SCStreamConfiguration()
            config.width = Int((CGFloat(display.width) * screen.backingScaleFactor).rounded())
            config.height = Int((CGFloat(display.height) * screen.backingScaleFactor).rounded())
            config.showsCursor = false
            let t = DispatchTime.now()
            let image = try await SCScreenshotManager.captureImage(contentFilter: filter, configuration: config)
            Log.debug("captured display \(id) '\(screen.localizedName)' frame=\(screen.frame) scale=\(screen.backingScaleFactor) -> \(image.width)x\(image.height) in \(Log.ms(since: t)) ms")
            result.append(DisplayCapture(screen: screen, image: image))
        }
        Log.debug("capture total \(Log.ms(since: start)) ms")
        return result
    }

    /// On-screen normal windows in z-order, as rectangles in each screen's top-left-origin point space.
    static func windowRects(for screen: NSScreen) -> [CGRect] {
        let ownPID = ProcessInfo.processInfo.processIdentifier
        guard let list = CGWindowListCopyWindowInfo([.optionOnScreenOnly, .excludeDesktopElements], kCGNullWindowID) as? [[String: Any]] else {
            return [CGRect(origin: .zero, size: screen.frame.size)]
        }
        let mainHeight = NSScreen.screens.first?.frame.height ?? screen.frame.height
        // Screen origin in CG global coordinates (top-left of the main display, y down).
        let screenTop = mainHeight - screen.frame.maxY
        var rects: [CGRect] = []
        for info in list {
            guard (info[kCGWindowLayer as String] as? Int) == 0,
                  (info[kCGWindowOwnerPID as String] as? Int32) != ownPID,
                  let dict = info[kCGWindowBounds as String] as? NSDictionary,
                  let r = CGRect(dictionaryRepresentation: dict) else { continue }
            let local = CGRect(x: r.minX - screen.frame.minX, y: r.minY - screenTop, width: r.width, height: r.height)
                .intersection(CGRect(origin: .zero, size: screen.frame.size))
            if local.width >= 8 && local.height >= 8 { rects.append(local) }
        }
        rects.append(CGRect(origin: .zero, size: screen.frame.size))
        return rects
    }
}
