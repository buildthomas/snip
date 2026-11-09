import Carbon
import Foundation
import SnipCore

/// Global hotkey via Carbon's RegisterEventHotKey: needs no Accessibility or Input Monitoring permission and only
/// ever sees its own key combination, never other keystrokes.
final class HotKey {
    static var onPress: (() -> Void)?
    private var hotKeyRef: EventHotKeyRef?
    private var handlerRef: EventHandlerRef?

    @discardableResult
    func register(_ spec: HotkeySpec) -> Bool {
        unregister()
        if handlerRef == nil {
            var eventType = EventTypeSpec(eventClass: OSType(kEventClassKeyboard), eventKind: UInt32(kEventHotKeyPressed))
            let status = InstallEventHandler(GetApplicationEventTarget(), { _, _, _ in
                Log.debug("hotkey pressed")
                DispatchQueue.main.async { HotKey.onPress?() }
                return noErr
            }, 1, &eventType, nil, &handlerRef)
            if status != noErr { Log.error("InstallEventHandler failed: \(status)") }
        }
        let id = EventHotKeyID(signature: OSType(0x534E_4950), id: 1)
        guard spec.hasRequiredModifier else {
            Log.error("refusing hotkey without Command/Option/Control: \(spec.spec)")
            return false
        }
        let status = RegisterEventHotKey(spec.keyCode, spec.carbonModifiers, id, GetApplicationEventTarget(), 0, &hotKeyRef)
        if status == noErr {
            Log.info("hotkey registered: \(spec.spec) (\(spec.display))")
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
}
