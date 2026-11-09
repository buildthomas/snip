import Carbon
import Foundation

/// Global hotkey via Carbon's RegisterEventHotKey: needs no Accessibility or Input Monitoring permission and only
/// ever sees its own key combination, never other keystrokes.
final class HotKey {
    static var onPress: (() -> Void)?
    private var hotKeyRef: EventHotKeyRef?
    private var handlerRef: EventHandlerRef?

    @discardableResult
    func register(_ choice: HotkeyChoice) -> Bool {
        unregister()
        if handlerRef == nil {
            var spec = EventTypeSpec(eventClass: OSType(kEventClassKeyboard), eventKind: UInt32(kEventHotKeyPressed))
            let status = InstallEventHandler(GetApplicationEventTarget(), { _, _, _ in
                Log.debug("hotkey pressed")
                DispatchQueue.main.async { HotKey.onPress?() }
                return noErr
            }, 1, &spec, nil, &handlerRef)
            if status != noErr { Log.error("InstallEventHandler failed: \(status)") }
        }
        let id = EventHotKeyID(signature: OSType(0x534E_4950), id: 1)
        let status = RegisterEventHotKey(choice.keyCode, UInt32(cmdKey | shiftKey), id, GetApplicationEventTarget(), 0, &hotKeyRef)
        if status == noErr {
            Log.info("hotkey registered: \(choice.rawValue)")
            return true
        }
        // -9878 (eventHotKeyExistsErr) usually means the system screenshot shortcut still owns this combination.
        Log.error("RegisterEventHotKey(\(choice.rawValue)) failed: \(status)")
        return false
    }

    func unregister() {
        if let ref = hotKeyRef { UnregisterEventHotKey(ref) }
        hotKeyRef = nil
    }
}
