import AppKit
import SnipCore

/// Small window that records the next key combination as the new global hotkey.
final class HotkeyRecorder: NSObject, NSWindowDelegate {
    private var panel: NSPanel?
    private var monitor: Any?
    private var pending: HotkeySpec?
    private let label = NSTextField(labelWithString: "")
    private let note = NSTextField(wrappingLabelWithString: "")
    private let saveButton = NSButton(title: "Save", target: nil, action: nil)
    private var completion: ((HotkeySpec?) -> Void)?

    func show(current: HotkeySpec, completion: @escaping (HotkeySpec?) -> Void) {
        self.completion = completion
        pending = nil

        let panel = NSPanel(contentRect: CGRect(x: 0, y: 0, width: 360, height: 170), styleMask: [.titled, .closable], backing: .buffered, defer: false)
        panel.title = "Snip hotkey"
        panel.level = .floating
        panel.delegate = self
        panel.isReleasedWhenClosed = false

        let title = NSTextField(labelWithString: "Press the new shortcut (current: \(current.display))")
        label.font = .systemFont(ofSize: 28, weight: .medium)
        label.alignment = .center
        label.stringValue = "…"
        note.textColor = .secondaryLabelColor
        note.font = .systemFont(ofSize: 11)
        saveButton.target = self
        saveButton.action = #selector(save)
        saveButton.keyEquivalent = "\r"
        saveButton.isEnabled = false
        let cancel = NSButton(title: "Cancel", target: self, action: #selector(cancel))

        let buttons = NSStackView(views: [cancel, saveButton])
        let stack = NSStackView(views: [title, label, note, buttons])
        stack.orientation = .vertical
        stack.spacing = 10
        stack.edgeInsets = NSEdgeInsets(top: 16, left: 16, bottom: 16, right: 16)
        stack.translatesAutoresizingMaskIntoConstraints = false
        panel.contentView = NSView()
        panel.contentView!.addSubview(stack)
        NSLayoutConstraint.activate([
            stack.leadingAnchor.constraint(equalTo: panel.contentView!.leadingAnchor),
            stack.trailingAnchor.constraint(equalTo: panel.contentView!.trailingAnchor),
            stack.topAnchor.constraint(equalTo: panel.contentView!.topAnchor),
            stack.bottomAnchor.constraint(equalTo: panel.contentView!.bottomAnchor),
            note.widthAnchor.constraint(equalToConstant: 328),
        ])

        monitor = NSEvent.addLocalMonitorForEvents(matching: .keyDown) { [weak self] event in
            self?.record(event)
            return nil
        }
        self.panel = panel
        NSApp.activate(ignoringOtherApps: true)
        panel.center()
        panel.makeKeyAndOrderFront(nil)
        Log.debug("hotkey recorder opened")
    }

    private func record(_ event: NSEvent) {
        let flags = event.modifierFlags.intersection(.deviceIndependentFlagsMask)
        // Plain Escape cancels; plain Return saves (the Save button's key equivalent doesn't see swallowed events).
        if flags.isDisjoint(with: [.command, .option, .control, .shift]) {
            if event.keyCode == 53 { cancel(); return }
            if event.keyCode == 36, pending != nil { save(); return }
        }
        let key = HotkeySpec.label(for: UInt32(event.keyCode)) ?? (event.charactersIgnoringModifiers ?? "?").uppercased()
        let spec = HotkeySpec(keyCode: UInt32(event.keyCode), command: flags.contains(.command), shift: flags.contains(.shift),
                              option: flags.contains(.option), control: flags.contains(.control), keyLabel: key)
        label.stringValue = spec.display
        if !spec.hasRequiredModifier {
            pending = nil
            note.stringValue = "Add ⌘, ⌥ or ⌃: without one, the hotkey would block normal typing."
        } else {
            pending = spec
            note.stringValue = spec.commonlyUsedByApps
                ? "Heads-up: many apps use \(spec.display) themselves. Snip will take it over everywhere."
                : "Press Save (or Return) to use this hotkey."
        }
        saveButton.isEnabled = pending != nil
        Log.debug("hotkey recorder: \(spec.spec) valid=\(pending != nil)")
    }

    @objc private func save() { finish(pending) }
    @objc private func cancel() { finish(nil) }

    func windowWillClose(_ notification: Notification) { finish(nil) }

    private func finish(_ result: HotkeySpec?) {
        if let monitor { NSEvent.removeMonitor(monitor) }
        monitor = nil
        guard let completion else { return }
        self.completion = nil
        panel?.delegate = nil
        panel?.close()
        panel = nil
        completion(result)
    }
}
