import Carbon
import Foundation
import SnipCore

/// Global hotkey via Carbon's RegisterEventHotKey: needs no Accessibility or Input Monitoring permission and only
/// ever sees its own key combinations, never other keystrokes.
final class HotKey {
    private static var handlers: [UInt32: () -> Void] = [:]
    private static var handlerRef: EventHandlerRef?
    private let id: UInt32
    private var hotKeyRef: EventHotKeyRef?

    init(id: UInt32, onPress: @escaping () -> Void) {
        self.id = id
        Self.handlers[id] = onPress
    }

    @discardableResult
    func register(_ spec: HotkeySpec) -> Bool {
        unregister()
        Self.installHandler()
        guard spec.hasRequiredModifier else {
            Log.error("refusing hotkey without Command/Option/Control: \(spec.spec)")
            return false
        }
        let hotKeyID = EventHotKeyID(signature: OSType(0x534E_4950), id: id)
        let status = RegisterEventHotKey(spec.keyCode, spec.carbonModifiers, hotKeyID, GetApplicationEventTarget(), 0, &hotKeyRef)
        if status == noErr {
            Log.info("hotkey \(id) registered: \(spec.spec) (\(spec.display))")
            return true
        }
        // -9878 (eventHotKeyExistsErr): another app or a system shortcut already owns this combination.
        Log.error("RegisterEventHotKey(\(spec.spec)) failed: \(status)")
        return false
    }

    func unregister() {
        if let ref = hotKeyRef { UnregisterEventHotKey(ref) }
        hotKeyRef = nil
    }

    private static func installHandler() {
        guard handlerRef == nil else { return }
        var eventType = EventTypeSpec(eventClass: OSType(kEventClassKeyboard), eventKind: UInt32(kEventHotKeyPressed))
        let status = InstallEventHandler(GetApplicationEventTarget(), { _, event, _ in
            var pressed = EventHotKeyID()
            GetEventParameter(event, EventParamName(kEventParamDirectObject), EventParamType(typeEventHotKeyID), nil,
                              MemoryLayout<EventHotKeyID>.size, nil, &pressed)
            let id = pressed.id
            Log.debug("hotkey \(id) pressed")
            DispatchQueue.main.async { HotKey.handlers[id]?() }
            return noErr
        }, 1, &eventType, nil, &handlerRef)
        if status != noErr { Log.error("InstallEventHandler failed: \(status)") }
    }
}
