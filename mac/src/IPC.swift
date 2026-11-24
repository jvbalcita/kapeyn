import Foundation

// The menu bar app listens on a local Mach message port; the "kapeyn" command sends one request
// and reads the reply. Only processes in the same login session can reach it.
let kIPCPortName = "com.kapeyn.Kapeyn.ipc"

/// Handles one command (on the main thread) and returns the reply text.
var ipcHandler: ((String) -> String)?

final class IPCServer {
    private var port: CFMessagePort?

    func start() {
        var context = CFMessagePortContext(version: 0, info: nil, retain: nil, release: nil, copyDescription: nil)
        var shouldFree: DarwinBoolean = false
        port = CFMessagePortCreateLocal(nil, kIPCPortName as CFString, { _, _, data, _ in
            let text = data.flatMap { String(data: $0 as Data, encoding: .utf8) } ?? ""
            let reply = ipcHandler?(text) ?? "error: Kapeyn isn't ready yet"
            return Unmanaged.passRetained(Data(reply.utf8) as CFData)
        }, &context, &shouldFree)
        if let p = port, let source = CFMessagePortCreateRunLoopSource(nil, p, 0) {
            CFRunLoopAddSource(CFRunLoopGetMain(), source, .commonModes)
        }
    }
}

enum IPCClient {
    /// Sends one command to the running app. Returns nil if it isn't reachable.
    static func send(_ text: String, timeout: Double = 5) -> String? {
        guard let port = CFMessagePortCreateRemote(nil, kIPCPortName as CFString) else { return nil }
        defer { CFMessagePortInvalidate(port) }
        var reply: Unmanaged<CFData>?
        let status = CFMessagePortSendRequest(port, 0, Data(text.utf8) as CFData, timeout, timeout,
                                              CFRunLoopMode.defaultMode.rawValue, &reply)
        guard status == kCFMessagePortSuccess, let data = reply?.takeRetainedValue() else { return nil }
        return String(data: data as Data, encoding: .utf8)
    }
}
