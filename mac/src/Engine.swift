import Foundation

/// One reason to stay awake. `key` identifies it (for snoozing), `label` is what the user sees.
struct Reason {
    let key: String
    let label: String
}

/// What to do when a session finishes by itself ("When it's done").
enum DoneAction: CaseIterable {
    case nothing, sleep, lock, shutDown

    /// "sleep", "lock", "shutdown" / "shut down", "nothing" / "none" -> the action; nil if not recognized.
    init?(word: String) {
        switch word.lowercased().replacingOccurrences(of: " ", with: "").replacingOccurrences(of: "-", with: "") {
        case "nothing", "none", "off": self = .nothing
        case "sleep", "suspend": self = .sleep
        case "lock": self = .lock
        case "shutdown", "poweroff": self = .shutDown
        default: return nil
        }
    }

    /// As the user reads it: "sleep", "lock", "shut down", "do nothing".
    var text: String {
        switch self {
        case .sleep: return "sleep"
        case .lock: return "lock"
        case .shutDown: return "shut down"
        case .nothing: return "do nothing"
        }
    }

    /// The word the app understands: "sleep", "lock", "shutdown", "nothing".
    var word: String {
        switch self {
        case .sleep: return "sleep"
        case .lock: return "lock"
        case .shutDown: return "shutdown"
        case .nothing: return "nothing"
        }
    }
}

/// What a "Keep awake until" session waits for.
enum UntilKind {
    case none, downloads, appCloses, commandEnds
}

/// What happened during one `Engine.step`, for the app to act on (lock, shutdown, notices).
struct StepEvents {
    var timerEnded = false      // a "Keep awake for" timer ran out
    var untilEnded = false      // a "Keep awake until" session finished (downloads done, app closed, command ended)
    var finished = ""           // ...what finished, as a sentence: "Downloads finished."
    var then = DoneAction.nothing // ...and what to do now (timer or "until" end)
    var workHoursEnded = false  // work hours just ended (seen live) and "Turn Kapeyn off" is on
    var wasManualOn = false     // ...and Kapeyn had been switched on by hand
    var lock = false            // ...and the computer should be locked
    var shutdown = false        // ...or shut down (after a countdown)
    var pauseStarted = false    // just paused because of low battery
}

/// Decides whether to stay awake and why. It makes no system calls and has no UI, so it can be tested:
/// the app gathers the inputs (time, calls, downloads, apps, battery), calls `step`, then acts on the result.
/// Kept in step with src/Engine.cs on Windows.
final class Engine {
    private let computer: String                 // "PC" or "Mac", for the status text
    private var snoozed = Set<String>()          // automatic reasons the user turned off
    private var lastAuto: [Reason] = []
    private var wasInWindow: Bool
    private var batteryOverride = false
    private var lastStep = Date.distantPast
    private var quietSince: Date?                // downloads: when traffic last stopped

    /// "Keep awake until downloads finish" ends after this long without downloading.
    static let downloadQuietMinutes = 2

    private(set) var manualOn = false
    private(set) var manualUntil: Date?
    private(set) var manualMinutes: Int?         // the "Keep awake for" choice
    private(set) var until = UntilKind.none      // the "Keep awake until" choice
    private(set) var untilTarget: String?        // ...the app's bundle id, or the command's pid
    private(set) var untilLabel: String?         // ...the app or command name the user sees
    /// What to do when the current timer or "until" session finishes. Resets once it's used or Kapeyn is
    /// turned off, so a "Shut down" choice never carries over to a later session.
    var then = DoneAction.nothing
    private(set) var awake = false
    private(set) var paused = false
    private(set) var pauseWhy = ""
    private(set) var reasons: [Reason] = []

    init(computer: String, inWorkHoursWindowNow: Bool) {
        self.computer = computer
        self.wasInWindow = inWorkHoursWindowNow
    }

    /// Switch on by hand, for a number of minutes or (nil) until switched off.
    func turnOn(minutes: Int?, now: Date) {
        manualOn = true
        manualUntil = minutes.map { now.addingTimeInterval(Double($0) * 60) }
        manualMinutes = minutes
        clearUntil()
        snoozed.removeAll()
        batteryOverride = batteryOverride || paused // turning on while paused overrides the battery pause
    }

    /// Switch on until something finishes: downloads (no target), an app closing (target = its bundle id)
    /// or a command ending (target = its process id).
    func turnOnUntil(_ kind: UntilKind, target: String?, label: String?, now: Date) {
        turnOn(minutes: nil, now: now)
        manualMinutes = nil
        until = kind
        untilTarget = target
        untilLabel = label
    }

    /// Switch off by hand. Automatic reasons active right now stay off until they end.
    func turnOff() {
        endSession()
        for r in lastAuto { snoozed.insert(r.key) }
        batteryOverride = false
    }

    private func clearUntil() {
        until = .none
        untilTarget = nil
        untilLabel = nil
        quietSince = nil
    }

    private func endSession() {
        manualOn = false
        manualUntil = nil
        clearUntil()
        then = .nothing
    }

    /// What the current "until" session is waiting for: "downloads finish", "Final Cut Pro closes".
    func untilText() -> String? {
        switch until {
        case .downloads: return "downloads finish"
        case .appCloses: return (untilLabel ?? "the app") + " closes"
        case .commandEnds: return (untilLabel ?? "the command") + " finishes"
        case .none: return nil
        }
    }

    func isSnoozed(_ key: String) -> Bool { return snoozed.contains(key) }
    func unsnooze(_ key: String) { snoozed.remove(key) }

    /// Start work-hours tracking from "now" (after waking from sleep or saving new hours),
    /// so an end that wasn't seen live never triggers turning off, locking or shutting down.
    func rebaseline(inWorkHoursWindowNow: Bool) { wasInWindow = inWorkHoursWindowNow }

    /// One evaluation. `inWindow` = inside the work-hours window (whether or not the schedule is on);
    /// `auto` = the other automatic reasons active now (call, downloading, apps, kapeyn run);
    /// `untilActive` = what a "Keep awake until" session waits for is still going (downloading, app open,
    /// command running).
    @discardableResult
    func step(settings s: Settings, now: Date, inWindow: Bool, auto: [Reason],
              onBattery: Bool, batteryPercent: Int, untilActive: Bool = true) -> StepEvents {
        var ev = StepEvents()
        let wasPaused = paused

        if manualOn, let end = manualUntil, now >= end {
            ev.timerEnded = true
            ev.finished = "Timer finished."
            ev.then = then
            endSession()
        } else if manualOn && until != .none {
            let done: Bool
            if until == .downloads {
                // Downloads come in bursts (one file after another), so wait for a quiet stretch.
                if untilActive { quietSince = nil } else if quietSince == nil { quietSince = now }
                done = quietSince.map { now.timeIntervalSince($0) >= Double(Engine.downloadQuietMinutes) * 60 } ?? false
            } else {
                done = !untilActive
            }
            if done {
                ev.untilEnded = true
                switch until {
                case .downloads: ev.finished = "Downloads finished."
                case .appCloses: ev.finished = (untilLabel ?? "The app") + " closed."
                default: ev.finished = (untilLabel ?? "The command") + " finished."
                }
                ev.then = then
                endSession()
            }
        }

        // Work hours ended? Only when seen live (the previous step was under 2 minutes ago), only when the
        // schedule is on, and only for a real end of the window, never for switching the schedule off.
        let fresh = now.timeIntervalSince(lastStep) < 120
        lastStep = now
        if s.scheduleEnabled && s.scheduleEndTurnsOff && wasInWindow && !inWindow && fresh {
            ev.workHoursEnded = true
            ev.wasManualOn = manualOn
            ev.shutdown = s.endShutdown
            ev.lock = s.endLock && !s.endShutdown
            endSession() // work hours have their own end action, so any "When it's done" choice is dropped
        }
        wasInWindow = inWindow

        var all: [Reason] = []
        if s.scheduleEnabled && inWindow { all.append(Reason(key: "hours", label: "work hours")) }
        all += auto
        lastAuto = all

        // Forget snoozes for reasons that have ended, so the next call/download wakes Kapeyn again.
        let keys = Set(all.map { $0.key })
        snoozed = snoozed.filter { keys.contains($0) }

        var active: [Reason] = []
        if manualOn {
            var label = manualUntil.map { Format.remaining($0.timeIntervalSince(now)) + " left" }
                ?? untilText().map { "until " + $0 }
                ?? "turned on"
            if then != .nothing && (manualUntil != nil || until != .none) { label += ", then " + then.text }
            active.append(Reason(key: "manual", label: label))
        }
        active += all.filter { !snoozed.contains($0.key) }
        reasons = active
        let wanted = !active.isEmpty

        // Low battery pause (clicking the cup overrides it until plugged in).
        paused = false
        pauseWhy = ""
        if !onBattery { batteryOverride = false }
        if wanted && s.pauseBatteryPercent > 0 && onBattery && batteryPercent >= 0
            && batteryPercent < s.pauseBatteryPercent && !batteryOverride {
            paused = true
            pauseWhy = "battery at \(batteryPercent)%"
        }
        ev.pauseStarted = paused && !wasPaused

        awake = wanted && !paused
        return ev
    }

    func statusText() -> String {
        if paused { return "Paused - " + pauseWhy }
        if !awake { return "Off - \(computer) may sleep" }
        // Just "Awake" when the only reason is that the user turned it on.
        if reasons.count == 1 && reasons[0].key == "manual" && manualUntil == nil && until == .none { return "Awake" }
        return "Awake - " + reasons.map { $0.label }.joined(separator: ", ")
    }
}
