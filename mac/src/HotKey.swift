import Carbon

/// Called on the main thread when the global shortcut (Control-Option-K) is pressed.
var hotKeyAction: (() -> Void)?

/// Global shortcut via Carbon - works without Accessibility permission.
final class HotKey {
    private var ref: EventHotKeyRef?
    private var handler: EventHandlerRef?

    func register() -> Bool {
        if ref != nil { return true }
        if handler == nil {
            var spec = EventTypeSpec(eventClass: OSType(kEventClassKeyboard), eventKind: UInt32(kEventHotKeyPressed))
            InstallEventHandler(GetApplicationEventTarget(), { _, _, _ in
                hotKeyAction?()
                return noErr
            }, 1, &spec, nil, &handler)
        }
        let id = EventHotKeyID(signature: OSType(0x4B50_594E), id: 1) // 'KPYN'
        let status = RegisterEventHotKey(UInt32(kVK_ANSI_K), UInt32(controlKey | optionKey), id,
                                         GetApplicationEventTarget(), 0, &ref)
        if status != noErr { ref = nil }
        return status == noErr
    }

    func unregister() {
        if let r = ref {
            UnregisterEventHotKey(r)
            ref = nil
        }
    }
}
