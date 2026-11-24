import AppKit
import IOKit.pwr_mgt

final class Power {
    private var sleepAssertion: IOPMAssertionID = 0
    private var activityAssertion: IOPMAssertionID = 0
    private var currentType: String?
    private var napBlocker: NSObjectProtocol?

    /// Holds (or releases) the keep-awake power assertion. Cheap to call repeatedly.
    func set(awake: Bool, display: Bool) {
        let want: String? = awake ? (display ? "PreventUserIdleDisplaySleep" : "PreventUserIdleSystemSleep") : nil
        if want == currentType { return }
        if sleepAssertion != 0 {
            IOPMAssertionRelease(sleepAssertion)
            sleepAssertion = 0
        }
        if let type = want {
            IOPMAssertionCreateWithName(type as CFString,
                                        IOPMAssertionLevel(kIOPMAssertionLevelOn),
                                        "Kapeyn is keeping your Mac awake" as CFString,
                                        &sleepAssertion)
        }
        currentType = want

        if awake && napBlocker == nil {
            // Keep our timer running on schedule (no App Nap) while active.
            napBlocker = ProcessInfo.processInfo.beginActivity(options: .userInitiatedAllowingIdleSystemSleep,
                                                               reason: "Kapeyn keep-awake timer")
        } else if !awake, let n = napBlocker {
            ProcessInfo.processInfo.endActivity(n)
            napBlocker = nil
        }
        if !awake && activityAssertion != 0 {
            IOPMAssertionRelease(activityAssertion)
            activityAssertion = 0
        }
    }

    func releaseAll() { set(awake: false, display: false) }

    /// Tells macOS "the user is active right now" - resets the idle timer (screen saver / lock).
    func declareUserActivity() {
        IOPMAssertionDeclareUserActivity("Kapeyn activity" as CFString, kIOPMUserActiveLocal, &activityAssertion)
    }

    static func idleSeconds() -> Double {
        if let anyInput = CGEventType(rawValue: ~0) { // kCGAnyInputEventType
            return CGEventSource.secondsSinceLastEventType(.combinedSessionState, eventType: anyInput)
        }
        let types: [CGEventType] = [.mouseMoved, .keyDown, .leftMouseDown, .rightMouseDown, .scrollWheel]
        return types.map { CGEventSource.secondsSinceLastEventType(.combinedSessionState, eventType: $0) }.min() ?? 0
    }

    /// 1px mouse move and back. Silently does nothing without Accessibility permission.
    static func wiggleMouse() {
        guard let here = CGEvent(source: nil)?.location else { return }
        let src = CGEventSource(stateID: .hidSystemState)
        for p in [CGPoint(x: here.x + 1, y: here.y), here] {
            CGEvent(mouseEventSource: src, mouseType: .mouseMoved, mouseCursorPosition: p, mouseButton: .left)?
                .post(tap: .cghidEventTap)
        }
    }
}
