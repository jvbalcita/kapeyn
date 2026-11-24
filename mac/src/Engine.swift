import Foundation

/// One reason to stay awake. `key` identifies it (for snoozing), `label` is what the user sees.
struct Reason {
    let key: String
    let label: String
}

/// What happened during one `Engine.step`, for the app to act on (lock, shutdown, notices).
struct StepEvents {
    var timerEnded = false      // a "Keep awake for" timer ran out
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

    private(set) var manualOn = false
    private(set) var manualUntil: Date?
    private(set) var manualMinutes: Int?         // the "Keep awake for" choice
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
        snoozed.removeAll()
        batteryOverride = batteryOverride || paused // turning on while paused overrides the battery pause
    }

    /// Switch off by hand. Automatic reasons active right now stay off until they end.
    func turnOff() {
        manualOn = false
        manualUntil = nil
        for r in lastAuto { snoozed.insert(r.key) }
        batteryOverride = false
    }

    func isSnoozed(_ key: String) -> Bool { return snoozed.contains(key) }
    func unsnooze(_ key: String) { snoozed.remove(key) }

    /// Start work-hours tracking from "now" (after waking from sleep or saving new hours),
    /// so an end that wasn't seen live never triggers turning off, locking or shutting down.
    func rebaseline(inWorkHoursWindowNow: Bool) { wasInWindow = inWorkHoursWindowNow }

    /// One evaluation. `inWindow` = inside the work-hours window (whether or not the schedule is on);
    /// `auto` = the other automatic reasons active now (call, downloading, apps, kapeyn run).
    @discardableResult
    func step(settings s: Settings, now: Date, inWindow: Bool, auto: [Reason],
              onBattery: Bool, batteryPercent: Int) -> StepEvents {
        var ev = StepEvents()
        let wasPaused = paused

        if manualOn, let until = manualUntil, now >= until {
            manualOn = false
            manualUntil = nil
            ev.timerEnded = true
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
            manualOn = false
            manualUntil = nil
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
            active.append(Reason(key: "manual",
                                 label: manualUntil.map { Format.remaining($0.timeIntervalSince(now)) + " left" } ?? "turned on"))
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
        if reasons.count == 1 && reasons[0].key == "manual" && manualUntil == nil { return "Awake" }
        return "Awake - " + reasons.map { $0.label }.joined(separator: ", ")
    }
}
