import AppKit
import AVFoundation
import ServiceManagement
import SnipCore

let snipVersion = "0.1.0-mac"
let bundleID = "com.buildthomas.snip"
let captureNotification = Notification.Name("com.buildthomas.snip.capture")
let hotkeyChangedNotification = Notification.Name("com.buildthomas.snip.hotkeyChanged")

final class AppDelegate: NSObject, NSApplicationDelegate {
    private var statusItem: NSStatusItem!
    private lazy var hotKey = HotKey(id: 1) { [weak self] in self?.capture(trigger: "hotkey") }
    private lazy var recordHotKey = HotKey(id: 2) { [weak self] in self?.record(trigger: "hotkey") }
    private var session: OverlaySession?
    private var recordSelect: RecordSelectSession?
    private var recording: RecordingController?
    private var capturing = false
    private var hotkeyInfoItem: NSMenuItem!
    private var recordItem: NSMenuItem!
    private var recordHotkeyInfoItem: NSMenuItem!
    private let recorder = HotkeyRecorder()
    private var loginItem: NSMenuItem!
    private var debugItem: NSMenuItem!

    func applicationDidFinishLaunching(_ notification: Notification) {
        Log.info("Snip \(snipVersion) started: \(Bundle.main.bundlePath) pid=\(ProcessInfo.processInfo.processIdentifier) macOS \(ProcessInfo.processInfo.operatingSystemVersionString)")
        Log.debug(Diagnostics.report())

        statusItem = NSStatusBar.system.statusItem(withLength: NSStatusItem.squareLength)
        statusItem.button?.image = NSImage(systemSymbolName: "scissors", accessibilityDescription: "Snip")
        statusItem.menu = buildMenu()
        NSApp.mainMenu = buildMainMenu()

        RecordingFiles.cleanUp()
        if !hotKey.register(Settings.shared.hotkey) {
            notify("Snip couldn't register \(Settings.shared.hotkey.display). Another app or a macOS shortcut (System Settings > Keyboard > Keyboard Shortcuts) probably uses it. Pick another one with Change hotkey in the Snip menu.")
        }
        if !recordHotKey.register(Settings.shared.recordHotkey) {
            Log.info("record hotkey \(Settings.shared.recordHotkey.display) unavailable; Record video stays in the menu")
        }
        DistributedNotificationCenter.default().addObserver(forName: hotkeyChangedNotification, object: nil, queue: .main) { [weak self] _ in
            guard let self else { return }
            let spec = Settings.shared.hotkey, record = Settings.shared.recordHotkey
            Log.info("hotkeys changed from the command line: \(spec.spec), record \(record.spec)")
            if !self.hotKey.register(spec) { self.notify("Snip couldn't register \(spec.display); it may be taken by another app or a macOS shortcut.") }
            if !self.recordHotKey.register(record) { self.notify("Snip couldn't register \(record.display); it may be taken by another app or a macOS shortcut.") }
        }

        DistributedNotificationCenter.default().addObserver(forName: captureNotification, object: nil, queue: .main) { [weak self] _ in
            self?.capture(trigger: "second launch")
        }

        setUpLoginItemOnFirstRun()
        if !Capture.hasPermission { Log.info("screen recording permission not granted yet") }
        if CommandLine.arguments.contains("--capture") { capture(trigger: "--capture") }
    }

    // MARK: - Menu

    private func buildMenu() -> NSMenu {
        let menu = NSMenu()
        menu.addItem(withTitle: "Capture", action: #selector(captureFromMenu), keyEquivalent: "").target = self
        recordItem = NSMenuItem(title: "Record video", action: #selector(recordFromMenu), keyEquivalent: "")
        recordItem.target = self
        menu.addItem(recordItem)
        menu.addItem(.separator())

        hotkeyInfoItem = NSMenuItem(title: "Hotkey: \(Settings.shared.hotkey.display)", action: nil, keyEquivalent: "")
        hotkeyInfoItem.isEnabled = false
        menu.addItem(hotkeyInfoItem)
        menu.addItem(withTitle: "Change hotkey…", action: #selector(changeHotkey), keyEquivalent: "").target = self
        recordHotkeyInfoItem = NSMenuItem(title: "", action: nil, keyEquivalent: "")
        recordHotkeyInfoItem.isEnabled = false
        menu.addItem(recordHotkeyInfoItem)
        menu.addItem(withTitle: "Change record hotkey…", action: #selector(changeRecordHotkey), keyEquivalent: "").target = self

        loginItem = NSMenuItem(title: "Start at login", action: #selector(toggleLogin), keyEquivalent: "")
        loginItem.target = self
        menu.addItem(loginItem)
        debugItem = NSMenuItem(title: "Debug logging", action: #selector(toggleDebug), keyEquivalent: "")
        debugItem.target = self
        menu.addItem(debugItem)
        menu.addItem(withTitle: "Open log", action: #selector(openLog), keyEquivalent: "").target = self
        menu.addItem(withTitle: "Write diagnostics to log", action: #selector(writeDiagnostics), keyEquivalent: "").target = self
        menu.addItem(.separator())
        menu.addItem(withTitle: "Quit Snip", action: #selector(quit), keyEquivalent: "q").target = self
        menu.delegate = self
        return menu
    }

    @objc private func captureFromMenu() {
        // Let the menu close first so it isn't in the capture.
        DispatchQueue.main.asyncAfter(deadline: .now() + 0.25) { self.capture(trigger: "menu") }
    }

    @objc private func recordFromMenu() {
        DispatchQueue.main.asyncAfter(deadline: .now() + 0.25) { self.record(trigger: "menu") }
    }

    @objc private func changeHotkey() {
        changeHotkey(hotKey, current: Settings.shared.hotkey, other: Settings.shared.recordHotkey) { Settings.shared.hotkey = $0 }
    }

    @objc private func changeRecordHotkey() {
        changeHotkey(recordHotKey, current: Settings.shared.recordHotkey, other: Settings.shared.hotkey) { Settings.shared.recordHotkey = $0 }
    }

    private func changeHotkey(_ key: HotKey, current old: HotkeySpec, other: HotkeySpec, save: @escaping (HotkeySpec) -> Void) {
        // Unregistered while recording, so pressing the current hotkey can be recorded instead of triggering it.
        key.unregister()
        recorder.show(current: old) { [weak self] spec in
            guard let self else { return }
            guard let spec, spec != old else {
                key.register(old)
                return
            }
            if spec == other {
                key.register(old)
                self.notify("\(spec.display) is Snip's other hotkey. Kept \(old.display).")
            } else if key.register(spec) {
                save(spec)
                Log.info("hotkey changed to \(spec.spec)")
            } else {
                key.register(old)
                self.notify("\(spec.display) is already used by another app or a macOS shortcut (System Settings > Keyboard > Keyboard Shortcuts). Kept \(old.display).")
            }
        }
    }

    /// Hidden main menu: overlay commands go through standard menu items, so App Shortcuts customised in
    /// System Settings > Keyboard > Keyboard Shortcuts (matched by menu title) apply to them.
    private func buildMainMenu() -> NSMenu {
        let main = NSMenu()
        func submenu(_ title: String, _ items: [(String, Selector, String, NSEvent.ModifierFlags)]) {
            let menu = NSMenu(title: title)
            for (t, action, key, mods) in items {
                if t == "-" { menu.addItem(.separator()); continue }
                let item = NSMenuItem(title: t, action: action, keyEquivalent: key)
                item.keyEquivalentModifierMask = mods
                menu.addItem(item)
            }
            let holder = NSMenuItem(title: title, action: nil, keyEquivalent: "")
            holder.submenu = menu
            main.addItem(holder)
        }
        submenu("Snip", [("Quit Snip", #selector(NSApplication.terminate(_:)), "q", .command)])
        submenu("File", [("Save…", #selector(OverlayView.snipSave(_:)), "s", .command)])
        submenu("Edit", [
            ("Undo", #selector(OverlayView.snipUndo(_:)), "z", .command),
            ("Redo", #selector(OverlayView.snipRedo(_:)), "z", [.command, .shift]),
            ("-", #selector(NSApplication.terminate(_:)), "", []),
            ("Copy", #selector(OverlayView.snipCopy(_:)), "c", .command),
            ("Paste", #selector(OverlayView.snipPaste(_:)), "v", .command),
            ("Select All", #selector(OverlayView.snipSelectAll(_:)), "a", .command),
        ])
        return main
    }

    @objc private func toggleLogin() {
        let service = SMAppService.mainApp
        do {
            if service.status == .enabled { try service.unregister() } else { try service.register() }
            Log.info("login item -> \(service.status.rawValue)")
        } catch {
            Log.error("login item change failed: \(error)")
            notify("Couldn't change Start at login: \(error.localizedDescription)")
        }
    }

    @objc private func toggleDebug() {
        Settings.shared.debugLogging.toggle()
        Log.info("debug logging: \(Settings.shared.debugLogging)")
    }

    @objc private func openLog() {
        Log.info("open log requested")
        NSWorkspace.shared.open(Log.file)
    }

    @objc private func writeDiagnostics() { Log.info(Diagnostics.report()) }

    @objc private func quit() {
        Log.info("quit")
        NSApp.terminate(nil)
    }

    // MARK: - Capture

    func capture(trigger: String) {
        if let recording {
            recording.stop()
            return
        }
        if let recordSelect {
            recordSelect.show()
            return
        }
        if let session {
            Log.debug("capture (\(trigger)) while overlay open: focusing it")
            session.show()
            return
        }
        guard !capturing else { return }
        Log.info("capture requested (\(trigger))")
        guard Capture.hasPermission || Capture.requestPermission() else {
            notify("Snip needs Screen Recording permission. Allow it in System Settings > Privacy & Security > Screen Recording, then quit and reopen Snip.")
            return
        }
        capturing = true
        let start = DispatchTime.now()
        Task { @MainActor in
            defer { capturing = false }
            do {
                let captures = try await Capture.captureAll()
                guard !captures.isEmpty else { notify("Nothing was captured; see the log."); return }
                let s = OverlaySession(captures: captures)
                s.onClose = { [weak self] in self?.session = nil }
                session = s
                s.show()
                Log.info("overlay visible \(Log.ms(since: start)) ms after trigger")
            } catch {
                Log.error("capture failed: \(error)")
                notify("Capture failed: \(error.localizedDescription)")
            }
        }
    }

    // MARK: - Recording

    /// Record hotkey: pick an area over the live screen, or stop the recording that's running.
    func record(trigger: String) {
        if let recording {
            recording.stop()
            return
        }
        if let recordSelect {
            recordSelect.show()
            return
        }
        if let session {
            session.show()
            return
        }
        Log.info("record requested (\(trigger))")
        guard Capture.hasPermission || Capture.requestPermission() else {
            notify("Snip needs Screen Recording permission. Allow it in System Settings > Privacy & Security > Screen Recording, then quit and reopen Snip.")
            return
        }
        let s = RecordSelectSession()
        s.onClose = { [weak self] in self?.recordSelect = nil }
        s.onRecord = { [weak self] screen, area in self?.startRecording(screen: screen, area: area) }
        recordSelect = s
        s.show()
    }

    private func startRecording(screen: NSScreen, area: CGRect) {
        guard Settings.shared.recordMicrophone else { beginRecording(screen: screen, area: area); return }
        switch AVCaptureDevice.authorizationStatus(for: .audio) {
        case .authorized:
            beginRecording(screen: screen, area: area)
        case .notDetermined:
            AVCaptureDevice.requestAccess(for: .audio) { granted in
                DispatchQueue.main.async {
                    if granted { self.beginRecording(screen: screen, area: area) } else { self.notifyMicrophoneDenied() }
                }
            }
        default:
            notifyMicrophoneDenied()
        }
    }

    private func notifyMicrophoneDenied() {
        notify("Snip isn't allowed to use the microphone. Allow it in System Settings > Privacy & Security > Microphone, or turn the microphone off for recordings.")
    }

    private func beginRecording(screen: NSScreen, area: CGRect) {
        let url: URL
        do { url = try RecordingFiles.newURL() } catch {
            notify("Couldn't start recording.\n\n\(error.localizedDescription)")
            return
        }
        let recorder = ScreenRecorder(screen: screen, area: area, url: url, systemSound: Settings.shared.recordSystemSound,
                                      microphone: Settings.shared.recordMicrophone)
        let controller = RecordingController(recorder: recorder)
        controller.onEnded = { [weak self] in self?.recording = nil }
        recording = controller
        controller.start()
    }

    func applicationWillTerminate(_ notification: Notification) {
        recording?.abort()
        RecordingResultWindow.discardAllOnQuit()
    }

    // MARK: - Helpers

    private func setUpLoginItemOnFirstRun() {
        guard !Settings.shared.loginItemInitialized else { return }
        // Only an installed copy claims Start at login (mirrors the Windows app).
        let path = Bundle.main.bundlePath
        guard path.hasSuffix(".app"), path.hasPrefix("/Applications") || path.hasPrefix(FileManager.default.homeDirectoryForCurrentUser.path + "/Applications") else {
            Log.debug("not an installed copy (\(path)); leaving Start at login alone")
            return
        }
        do {
            try SMAppService.mainApp.register()
            Settings.shared.loginItemInitialized = true
            Log.info("registered Start at login")
        } catch {
            Log.error("Start at login registration failed: \(error)")
        }
    }

    private func notify(_ text: String) {
        Log.info("message to user: \(text)")
        let a = NSAlert()
        a.messageText = "Snip"
        a.informativeText = text
        NSApp.activate(ignoringOtherApps: true)
        a.runModal()
    }
}

extension AppDelegate: NSMenuDelegate {
    func menuWillOpen(_ menu: NSMenu) {
        hotkeyInfoItem.title = "Hotkey: \(Settings.shared.hotkey.display)"
        recordHotkeyInfoItem.title = "Record hotkey: \(Settings.shared.recordHotkey.display)"
        recordItem.title = recording == nil ? "Record video" : "Stop recording"
        loginItem.state = SMAppService.mainApp.status == .enabled ? .on : .off
        debugItem.state = Settings.shared.debugLogging ? .on : .off
    }
}
