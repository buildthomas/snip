import AppKit
import ServiceManagement
import SnipCore

let args = CommandLine.arguments
if args.contains("--version") {
    print("Snip \(snipVersion)")
    exit(0)
}
if let flag = ["--set-hotkey", "--set-record-hotkey"].first(where: { args.contains($0) }), let i = args.firstIndex(of: flag) {
    guard i + 1 < args.count, let spec = HotkeySpec.parse(args[i + 1]) else {
        print("Usage: Snip \(flag) <modifiers+key>, e.g. cmd+shift+s, ctrl+opt+2, cmd+f5")
        exit(2)
    }
    guard spec.hasRequiredModifier else {
        print("\(spec.display) needs Command, Option or Control; otherwise it would block normal typing.")
        exit(2)
    }
    let record = flag == "--set-record-hotkey"
    guard spec != (record ? Settings.shared.hotkey : Settings.shared.recordHotkey) else {
        print("\(spec.display) is already Snip's other hotkey.")
        exit(2)
    }
    if record { Settings.shared.recordHotkey = spec } else { Settings.shared.hotkey = spec }
    DistributedNotificationCenter.default().postNotificationName(hotkeyChangedNotification, object: nil, userInfo: nil, deliverImmediately: true)
    Log.info("hotkey set from the command line: \(spec.spec)")
    Log.flush()
    print("Hotkey set to \(spec.display) (\(spec.spec)). A running Snip switches right away; check the log if it reports a conflict.")
    if spec.commonlyUsedByApps { print("Note: many apps use \(spec.display) themselves; Snip takes it over everywhere.") }
    exit(0)
}
if args.contains("--unregister-login") {
    try? SMAppService.mainApp.unregister()
    print("Start at login: \(SMAppService.mainApp.status == .enabled ? "still enabled" : "off")")
    exit(0)
}
if args.contains("--diagnostics") {
    _ = NSApplication.shared
    print(Diagnostics.report())
    exit(0)
}

// Single instance: a second launch asks the running copy to capture, then exits.
if let id = Bundle.main.bundleIdentifier,
   NSRunningApplication.runningApplications(withBundleIdentifier: id).contains(where: { $0.processIdentifier != ProcessInfo.processInfo.processIdentifier }) {
    Log.info("already running; asking it to capture")
    DistributedNotificationCenter.default().postNotificationName(captureNotification, object: nil, userInfo: nil, deliverImmediately: true)
    Log.flush()
    exit(0)
}

let app = NSApplication.shared
let delegate = AppDelegate()
app.delegate = delegate
app.setActivationPolicy(.accessory)
app.run()
