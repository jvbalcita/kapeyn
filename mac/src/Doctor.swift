import AppKit
import ApplicationServices
import IOKit

/// One thing on this Mac that can lock it, blank the screen or put it to sleep.
struct Finding {
    var what: String
    var seconds: Int
    var inputBased: Bool // only activity resets it (a power assertion doesn't)
    var byOrg: Bool      // managed by your organization (MDM profile)
    var kind: String     // "saver", "display", "sleep"
}

/// "Lock doctor": finds what will lock or sleep this Mac, and how Kapeyn covers each one.
enum Doctor {
    static func shell(_ path: String, _ args: [String]) -> String {
        let p = Process()
        p.executableURL = URL(fileURLWithPath: path)
        p.arguments = args
        let pipe = Pipe()
        p.standardOutput = pipe
        p.standardError = pipe
        do { try p.run() } catch { return "" }
        let data = pipe.fileHandleForReading.readDataToEndOfFile()
        p.waitUntilExit()
        return String(data: data, encoding: .utf8) ?? ""
    }

    static func scan() -> [Finding] {
        var out: [Finding] = []

        // Password after screen saver / display sleep (macOS 13+: sysadminctl prints to stderr).
        let lock = shell("/usr/sbin/sysadminctl", ["-screenLock", "status"])
        var lockText = ""
        if lock.contains("immediate") {
            lockText = ", then a password is required"
        } else if let r = lock.range(of: "delay is [0-9]+", options: .regularExpression),
                  let n = Int(lock[r].split(separator: " ").last ?? "") {
            lockText = ", then a password is required after \(Format.duration(n))"
        }

        // Screen saver (a managed profile wins over the user's own setting).
        let domain = "com.apple.screensaver" as CFString
        let forced = CFPreferencesAppValueIsForced("idleTime" as CFString, domain)
        var idle = (CFPreferencesCopyAppValue("idleTime" as CFString, domain) as? NSNumber)?.intValue
        if idle == nil {
            idle = Int(shell("/usr/bin/defaults", ["-currentHost", "read", "com.apple.screensaver", "idleTime"])
                .trimmingCharacters(in: .whitespacesAndNewlines))
        }
        if let i = idle, i > 0 {
            out.append(Finding(what: "Screen saver starts" + lockText, seconds: i, inputBased: true, byOrg: forced, kind: "saver"))
        }

        // Display sleep and system sleep from the current power settings.
        let pm = shell("/usr/bin/pmset", ["-g"])
        func pmValue(_ key: String) -> Int? {
            for line in pm.split(separator: "\n") {
                let parts = line.split(whereSeparator: { $0 == " " || $0 == "\t" })
                if parts.count >= 2 && parts[0] == key { return Int(parts[1]) }
            }
            return nil
        }
        if let d = pmValue("displaysleep"), d > 0 {
            out.append(Finding(what: "Display sleeps" + lockText, seconds: d * 60, inputBased: false, byOrg: false, kind: "display"))
        }
        if let s = pmValue("sleep"), s > 0 {
            out.append(Finding(what: "Mac goes to sleep", seconds: s * 60, inputBased: false, byOrg: false, kind: "sleep"))
        }
        return out.sorted { $0.seconds < $1.seconds }
    }

    /// Nudge a bit before the shortest input-based timer fires (and under Teams' 5-minute "Away").
    static func recommendedNudgeSeconds(_ findings: [Finding]) -> Int {
        let timers = findings.filter { $0.inputBased && $0.seconds > 0 }.map { $0.seconds }
        guard let shortest = timers.min() else { return 120 }
        return max(30, min(240, shortest / 2))
    }

    static func hidIdleSeconds() -> Double? {
        let service = IOServiceGetMatchingService(kIOMainPortDefault, IOServiceMatching("IOHIDSystem"))
        guard service != 0 else { return nil }
        defer { IOObjectRelease(service) }
        guard let value = IORegistryEntryCreateCFProperty(service, "HIDIdleTime" as CFString, kCFAllocatorDefault, 0)?
            .takeRetainedValue() as? NSNumber else { return nil }
        return value.doubleValue / 1_000_000_000
    }

    static func report(settings s: Settings, power: Power, awake: Bool, status: String,
                       nudgeSeconds: Int, autoNudge: Bool) -> String {
        let findings = scan()
        var problems: [String] = []
        var lines: [String] = []

        lines.append("KAPEYN LOCK DOCTOR")
        lines.append("==================")
        lines.append("")
        lines.append("Timers on this Mac that can lock it, blank the screen or put it to sleep:")
        if findings.isEmpty { lines.append("  (none found - nothing here locks or sleeps this Mac on a timer)") }
        for f in findings {
            lines.append("")
            lines.append("  * \(f.what): after \(Format.duration(f.seconds))" + (f.byOrg ? "   [set by your organization]" : ""))
            lines.append("      " + coverage(f, s, nudgeSeconds, &problems))
        }

        lines.append("")
        lines.append("Kapeyn right now:")
        lines.append("  * Status: \(status)")
        let assertions = shell("/usr/bin/pmset", ["-g", "assertions"])
        lines.append("  * macOS sees Kapeyn's keep-awake assertion: " + (assertions.contains("Kapeyn") ? "yes" : "no"))
        lines.append("  * Activity signal: " + (s.simulateActivity
            ? "on - sent after \(Format.duration(nudgeSeconds)) of idle" + (autoNudge ? " (Auto: picked from the timers above)" : "")
            : "off"))
        if s.simulateActivity {
            lines.append("  * Mouse nudge (for Teams/Slack): " + (s.wiggleMouse
                ? (AXIsProcessTrusted() ? "on" : "on, but Accessibility permission is missing [!]")
                : "off"))
            if s.wiggleMouse && !AXIsProcessTrusted() {
                problems.append("Mouse nudge needs Accessibility permission: System Settings > Privacy & Security > Accessibility > Kapeyn.")
            }
        }
        // Self-test: does the activity signal reset macOS's idle timer?
        let before = hidIdleSeconds()
        power.declareUserActivity()
        usleep(100_000)
        if let after = hidIdleSeconds() {
            let ok = after < 1 || (before.map { after < $0 } ?? false)
            lines.append("  * Self-test: idle timer after activity signal = \(String(format: "%.1f", after)) s " + (ok ? "[OK]" : "[?]"))
        }

        if !awake { problems.insert("Kapeyn is off right now, so none of this protection is active. Click the cup to turn it on.", at: 0) }

        lines.append("")
        lines.append("Verdict:")
        if problems.isEmpty { lines.append("  [OK] Kapeyn covers every timer found on this Mac.") }
        for p in problems { lines.append("  [!] \(p)") }
        return lines.joined(separator: "\n")
    }

    private static func coverage(_ f: Finding, _ s: Settings, _ nudge: Int, _ problems: inout [String]) -> String {
        if f.inputBased {
            if !s.simulateActivity {
                problems.append("\"\(f.what)\" only resets with activity. Turn on Settings > Prevent screen lock & Away status.")
                return "[!] Not covered: only activity prevents this, and it's off."
            }
            if nudge >= f.seconds {
                problems.append("Kapeyn signals activity after \(Format.duration(nudge)), which is too late for \"\(f.what)\". Set Settings > Nudge when idle for > Auto.")
                return "[!] Not covered: Kapeyn's activity signal comes too late."
            }
            return "[OK] Covered: Kapeyn signals activity after \(Format.duration(nudge)) of idle."
        }
        if f.kind == "display" {
            return s.keepScreenOn || s.simulateActivity
                ? "[OK] Covered: Kapeyn keeps the display awake."
                : "[i] Kapeyn lets the display sleep (Settings > Keep screen on is off)."
        }
        return "[OK] Covered: Kapeyn's keep-awake assertion blocks idle sleep (closing a MacBook's lid still sleeps it)."
    }
}

enum Format {
    static func duration(_ seconds: Int) -> String {
        if seconds < 60 { return "\(seconds) s" }
        if seconds < 3600 {
            let m = seconds / 60, s = seconds % 60
            return s == 0 ? "\(m) min" : "\(m) min \(s) s"
        }
        let h = seconds / 3600, m = (seconds % 3600) / 60
        return m == 0 ? "\(h) h" : "\(h) h \(m) min"
    }

    static func remaining(_ seconds: TimeInterval) -> String {
        let total = max(1, Int((seconds / 60).rounded(.up)))
        let h = total / 60, m = total % 60
        if h == 0 { return "\(total) min" }
        return m == 0 ? "\(h) h" : "\(h) h \(m) min"
    }

    static func minutes(_ minutes: Int) -> String {
        if minutes < 60 { return "\(minutes) minutes" }
        let h = minutes / 60
        return h == 1 ? "1 hour" : "\(h) hours"
    }

    static func hour(_ hour: Int) -> String {
        if hour == 24 { return "midnight" }
        var c = DateComponents()
        c.hour = hour
        c.minute = 0
        guard let date = Calendar.current.date(from: c) else { return "\(hour):00" }
        let f = DateFormatter()
        f.timeStyle = .short
        f.dateStyle = .none
        return f.string(from: date)
    }

    static func ago(_ date: Date) -> String {
        if date == .distantPast { return "not yet" }
        return duration(max(0, Int(Date().timeIntervalSince(date)))) + " ago"
    }

    static func days(_ mask: Int) -> String {
        if mask == 0x3E { return "Mon-Fri" }
        if mask == 0x7F { return "every day" }
        let names = ["Sun", "Mon", "Tue", "Wed", "Thu", "Fri", "Sat"]
        let on = (1...7).map { $0 % 7 }.filter { mask & (1 << $0) != 0 }.map { names[$0] }
        return on.isEmpty ? "no days" : on.joined(separator: ", ")
    }
}
