import AppKit
import ServiceManagement

let snipVersion = "0.1.0-mac"
let bundleID = "com.buildthomas.snip"
let captureNotification = Notification.Name("com.buildthomas.snip.capture")

final class AppDelegate: NSObject, NSApplicationDelegate {
    private var statusItem: NSStatusItem!
    private let hotKey = HotKey()
    private var session: OverlaySession?
    private var capturing = false
    private var hotkeyItems: [NSMenuItem] = []
    private var loginItem: NSMenuItem!
    private var debugItem: NSMenuItem!

    func applicationDidFinishLaunching(_ notification: Notification) {
        Log.info("Snip \(snipVersion) started: \(Bundle.main.bundlePath) pid=\(ProcessInfo.processInfo.processIdentifier) macOS \(ProcessInfo.processInfo.operatingSystemVersionString)")
        Log.debug(Diagnostics.report())

        statusItem = NSStatusBar.system.statusItem(withLength: NSStatusItem.squareLength)
        statusItem.button?.image = NSImage(systemSymbolName: "scissors", accessibilityDescription: "Snip")
        statusItem.menu = buildMenu()

        HotKey.onPress = { [weak self] in self?.capture(trigger: "hotkey") }
        if !hotKey.register(Settings.shared.hotkey) {
            notify("Snip couldn't register \(Settings.shared.hotkey.title). If macOS uses it for screenshots, turn that shortcut off in System Settings > Keyboard > Keyboard Shortcuts > Screenshots, or pick another hotkey from the Snip menu.")
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
        menu.addItem(.separator())

        let hotkeyMenu = NSMenu()
        for choice in HotkeyChoice.allCases {
            let item = NSMenuItem(title: choice.title, action: #selector(chooseHotkey(_:)), keyEquivalent: "")
            item.target = self
            item.representedObject = choice.rawValue
            hotkeyItems.append(item)
            hotkeyMenu.addItem(item)
        }
        let hotkeyItem = NSMenuItem(title: "Hotkey", action: nil, keyEquivalent: "")
        hotkeyItem.submenu = hotkeyMenu
        menu.addItem(hotkeyItem)

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

    @objc private func chooseHotkey(_ sender: NSMenuItem) {
        guard let raw = sender.representedObject as? String, let choice = HotkeyChoice(rawValue: raw) else { return }
        if hotKey.register(choice) {
            Settings.shared.hotkey = choice
        } else {
            hotKey.register(Settings.shared.hotkey)
            notify("\(choice.title) is taken, most likely by macOS's own screenshot shortcut. Turn it off in System Settings > Keyboard > Keyboard Shortcuts > Screenshots, then pick it again.")
        }
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
        for item in hotkeyItems { item.state = (item.representedObject as? String) == Settings.shared.hotkey.rawValue ? .on : .off }
        loginItem.state = SMAppService.mainApp.status == .enabled ? .on : .off
        debugItem.state = Settings.shared.debugLogging ? .on : .off
    }
}
