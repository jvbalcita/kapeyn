import Foundation

// MARK: helpers

/// A local date/time (so schedule tests mean the same thing in every time zone).
func at(_ y: Int, _ m: Int, _ d: Int, _ h: Int = 0, _ mi: Int = 0, _ s: Int = 0) -> Date {
    var c = DateComponents()
    c.year = y; c.month = m; c.day = d; c.hour = h; c.minute = mi; c.second = s
    return Calendar.current.date(from: c)!
}

/// Settings backed by a throwaway defaults domain, so tests never touch the real app's settings.
func makeSettings(_ prepare: (UserDefaults) -> Void = { _ in }) -> (Settings, () -> Void) {
    let name = "kapeyn-tests-\(UUID().uuidString)"
    let d = UserDefaults(suiteName: name)!
    prepare(d)
    return (Settings(defaults: d), { d.removePersistentDomain(forName: name) })
}

// MARK: work hours

func scheduleTests() {
    let (s, cleanup) = makeSettings()
    defer { cleanup() }
    func inside(_ day: Int, _ h: Int, _ m: Int = 0) -> Bool { return Triggers.inWorkHours(s, at(2026, 11, 23 + day, h, m)) } // 23 = Monday

    s.scheduleDays = 0x3E; s.scheduleStartMin = 13 * 60; s.scheduleEndMin = 22 * 60
    check("Mon 12:59 is before work", !inside(0, 12, 59))
    check("Mon 13:00 is the first working minute", inside(0, 13))
    check("Mon 21:59 is the last working minute", inside(0, 21, 59))
    check("Mon 22:00 is after work", !inside(0, 22))
    check("Saturday is off", !inside(5, 15))
    check("Sunday is off", !inside(6, 15))

    s.scheduleStartMin = 22 * 60; s.scheduleEndMin = 6 * 60
    check("overnight: Mon 23:00 inside", inside(0, 23))
    check("overnight: Tue 02:00 inside (Monday's shift)", inside(1, 2))
    check("overnight: Mon 02:00 outside (Sunday's shift is off)", !inside(0, 2))
    check("overnight: Sat 02:00 inside (Friday's shift)", inside(5, 2))
    check("overnight: Sun 02:00 outside (Saturday's shift is off)", !inside(6, 2))
    check("overnight: Tue 06:00 is the end", !inside(1, 6))

    s.scheduleStartMin = 18 * 60; s.scheduleEndMin = 0
    check("to midnight: Mon 23:59 inside", inside(0, 23, 59))
    check("to midnight: Tue 00:00 outside", !inside(1, 0))

    s.scheduleStartMin = 9 * 60 + 30; s.scheduleEndMin = 17 * 60 + 45
    check("minutes: 9:29 outside", !inside(0, 9, 29))
    check("minutes: 9:30 inside", inside(0, 9, 30))
    check("minutes: 17:45 outside", !inside(0, 17, 45))

    s.scheduleDays = 0
    check("no days ticked: never inside", !inside(0, 12))
    s.scheduleDays = 0x41
    check("weekend only: Saturday inside", inside(5, 12))
}

// MARK: engine

let t0 = at(2026, 11, 23, 10, 0, 0)

func engineSettings() -> (Settings, () -> Void) {
    return makeSettings { $0.set(20, forKey: "PauseBatteryPercent") }
}

@discardableResult
func step(_ e: Engine, _ s: Settings, _ t: Date, _ auto: [Reason] = [], inWindow: Bool = false,
          onBattery: Bool = false, pct: Int = -1) -> StepEvents {
    return e.step(settings: s, now: t, inWindow: inWindow, auto: auto, onBattery: onBattery, batteryPercent: pct)
}

func engineBasics() {
    let (s, cleanup) = engineSettings()
    defer { cleanup() }
    let e = Engine(computer: "Mac", inWorkHoursWindowNow: false)
    step(e, s, t0)
    check("starts off", !e.awake)
    eq("status when off", "Off - Mac may sleep", e.statusText())

    e.turnOn(minutes: nil, now: t0)
    step(e, s, t0)
    check("turned on: awake", e.awake)
    eq("status when on by hand", "Awake", e.statusText())

    e.turnOff()
    step(e, s, t0)
    check("turned off: not awake", !e.awake)

    e.turnOn(minutes: 60, now: t0)
    step(e, s, t0)
    eq("timed: status shows time left", "Awake - 1 h left", e.statusText())
    eq("timed: remembers the choice", 60, e.manualMinutes ?? -1)
    step(e, s, t0.addingTimeInterval(18 * 60))
    eq("timed: counts down", "Awake - 42 min left", e.statusText())
    var ev = step(e, s, t0.addingTimeInterval(59 * 60 + 30))
    check("timed: still awake just before the end", e.awake && !ev.timerEnded)
    ev = step(e, s, t0.addingTimeInterval(60 * 60))
    check("timed: switches off at the end", !e.awake && ev.timerEnded)
    ev = step(e, s, t0.addingTimeInterval(61 * 60))
    check("timed: 'timer ended' is reported once", !ev.timerEnded)

    e.turnOn(minutes: 30, now: t0)
    e.turnOn(minutes: 240, now: t0)
    step(e, s, t0)
    eq("picking another time restarts the countdown", "Awake - 4 h left", e.statusText())
}

func engineSnooze() {
    let (s, cleanup) = engineSettings()
    defer { cleanup() }
    let call = [Reason(key: "call", label: "on a call")]
    let e = Engine(computer: "Mac", inWorkHoursWindowNow: false)

    step(e, s, t0, call)
    check("a call keeps it awake", e.awake)
    eq("status says why", "Awake - on a call", e.statusText())
    e.turnOff()
    step(e, s, t0.addingTimeInterval(5), call)
    check("turning off during a call stays off (snoozed)", !e.awake && e.isSnoozed("call"))
    step(e, s, t0.addingTimeInterval(10))
    check("call ended: snooze forgotten", !e.isSnoozed("call"))
    step(e, s, t0.addingTimeInterval(15), call)
    check("the next call wakes it again", e.awake)

    e.turnOff()
    step(e, s, t0.addingTimeInterval(20), call)
    e.turnOn(minutes: nil, now: t0.addingTimeInterval(25))
    step(e, s, t0.addingTimeInterval(25), call)
    check("turning on clears snoozes", e.awake && !e.isSnoozed("call"))
    eq("manual plus call in the status", "Awake - turned on, on a call", e.statusText())

    let e2 = Engine(computer: "Mac", inWorkHoursWindowNow: false)
    let run = [Reason(key: "cli:4242", label: "running npm")]
    step(e2, s, t0, run)
    eq("kapeyn run shows in the status", "Awake - running npm", e2.statusText())
    e2.turnOff()
    step(e2, s, t0.addingTimeInterval(5), run)
    check("kapeyn run can be snoozed", !e2.awake)
    e2.unsnooze("cli:4242")
    step(e2, s, t0.addingTimeInterval(10), run)
    check("a new 'hold' unsnoozes it", e2.awake)
}

func engineWorkHoursEnd() {
    let (s, cleanup) = engineSettings()
    defer { cleanup() }
    s.scheduleEnabled = true

    var e = Engine(computer: "Mac", inWorkHoursWindowNow: true)
    e.turnOn(minutes: nil, now: t0)
    step(e, s, t0, inWindow: true)
    check("in work hours: awake with the schedule reason", e.awake && e.reasons.contains { $0.key == "hours" })
    var ev = step(e, s, t0.addingTimeInterval(5), inWindow: false)
    check("end seen live: reported", ev.workHoursEnded && ev.wasManualOn)
    check("end: no lock or shutdown by default", !ev.lock && !ev.shutdown)
    check("end: turned itself off", !e.manualOn && !e.awake)
    ev = step(e, s, t0.addingTimeInterval(10), inWindow: false)
    check("end is reported only once", !ev.workHoursEnded)

    s.endLock = true
    e = Engine(computer: "Mac", inWorkHoursWindowNow: true)
    step(e, s, t0, inWindow: true)
    ev = step(e, s, t0.addingTimeInterval(5), inWindow: false)
    check("'also lock' asks to lock", ev.lock && !ev.shutdown)

    s.endShutdown = true
    e = Engine(computer: "Mac", inWorkHoursWindowNow: true)
    step(e, s, t0, inWindow: true)
    ev = step(e, s, t0.addingTimeInterval(5), inWindow: false)
    check("'also shut down' wins over lock", ev.shutdown && !ev.lock)
    s.endLock = false
    s.endShutdown = false

    e = Engine(computer: "Mac", inWorkHoursWindowNow: true)
    e.turnOn(minutes: nil, now: t0)
    step(e, s, t0, inWindow: true)
    ev = step(e, s, t0.addingTimeInterval(30 * 60), inWindow: false)
    check("asleep across the end (30 min gap): nothing happens", !ev.workHoursEnded && e.manualOn)

    e = Engine(computer: "Mac", inWorkHoursWindowNow: true)
    step(e, s, t0, inWindow: true)
    e.rebaseline(inWorkHoursWindowNow: false)
    ev = step(e, s, t0.addingTimeInterval(5), inWindow: false)
    check("after waking or saving new hours (rebaseline): nothing happens", !ev.workHoursEnded)

    s.scheduleEnabled = false
    e = Engine(computer: "Mac", inWorkHoursWindowNow: true)
    step(e, s, t0, inWindow: true)
    ev = step(e, s, t0.addingTimeInterval(5), inWindow: false)
    check("schedule switched off: never fires", !ev.workHoursEnded)

    s.scheduleEnabled = true
    s.scheduleEndTurnsOff = false
    e = Engine(computer: "Mac", inWorkHoursWindowNow: true)
    e.turnOn(minutes: nil, now: t0)
    step(e, s, t0, inWindow: true)
    ev = step(e, s, t0.addingTimeInterval(5), inWindow: false)
    check("'Turn Kapeyn off' unticked: no action, stays on", !ev.workHoursEnded && e.awake)
}

func engineBattery() {
    let (s, cleanup) = engineSettings()
    defer { cleanup() }
    let e = Engine(computer: "Mac", inWorkHoursWindowNow: false)
    e.turnOn(minutes: nil, now: t0)

    var ev = step(e, s, t0, onBattery: true, pct: 15)
    check("on battery below 20%: paused", e.paused && !e.awake && ev.pauseStarted)
    eq("status explains the pause", "Paused - battery at 15%", e.statusText())
    ev = step(e, s, t0.addingTimeInterval(5), onBattery: true, pct: 15)
    check("pause is reported only once", !ev.pauseStarted)
    e.turnOn(minutes: nil, now: t0.addingTimeInterval(10))
    step(e, s, t0.addingTimeInterval(10), onBattery: true, pct: 14)
    check("clicking the cup overrides the pause", e.awake && !e.paused)
    step(e, s, t0.addingTimeInterval(15), onBattery: false, pct: 14)
    ev = step(e, s, t0.addingTimeInterval(20), onBattery: true, pct: 14)
    check("after plugging in and out again, it pauses again", e.paused && ev.pauseStarted)

    s.pauseBatteryPercent = 0
    let e2 = Engine(computer: "Mac", inWorkHoursWindowNow: false)
    e2.turnOn(minutes: nil, now: t0)
    step(e2, s, t0, onBattery: true, pct: 3)
    check("'Never' setting: no pause even at 3%", e2.awake)
}

// MARK: formatting, parsing

func formatTests() {
    eq("45 s", "45 s", Format.duration(45))
    eq("1 min 30 s", "1 min 30 s", Format.duration(90))
    eq("2 min 30 s (auto nudge)", "2 min 30 s", Format.duration(150))
    eq("1 h 30 min", "1 h 30 min", Format.duration(5400))
    eq("time left rounds up", "43 min", Format.remaining(42.2 * 60))
    eq("under a minute shows 1 min", "1 min", Format.remaining(20))
    eq("1 h 1 min", "1 h 1 min", Format.remaining(61 * 60))
    eq("menu: 1 hour", "1 hour", Format.minutes(60))
    eq("menu: 8 hours", "8 hours", Format.minutes(480))
    eq("Mon-Fri", "Mon-Fri", Format.days(0x3E))
    eq("every day", "every day", Format.days(0x7F))
    eq("weekend (Monday-first order)", "Sat, Sun", Format.days(0x41))
    eq("no days", "no days", Format.days(0))
    // Times follow the Mac's own format (12/24 h); check the parts rather than exact spacing.
    let one = Format.timeOfDay(13 * 60)
    check("1 PM is shown as 1 PM or 13:00", one.contains("1:00") || one.contains("13:00"), one)
    let (s, cleanup) = makeSettings()
    defer { cleanup() }
    s.scheduleStartMin = 22 * 60
    s.scheduleEndMin = 6 * 60
    check("overnight summary says next day", Format.schedule(s).hasSuffix("(next day)"), Format.schedule(s))
}

func durationTests() {
    let good: [(String, Int)] = [("30m", 30), ("2h", 120), ("1h30m", 90), ("90", 90), ("45min", 45),
                                 ("2hrs", 120), ("1 hour 30 minutes", 90), ("1H30M", 90)]
    for (text, minutes) in good {
        let got = CLI.parseDuration(text)
        check("\"\(text)\" = \(minutes) min", got == minutes, got.map { "got \($0)" } ?? "not understood")
    }
    for text in ["", "0", "-5", "banana", "1h30", "30s"] {
        check("\"\(text)\" is rejected", CLI.parseDuration(text) == nil)
    }
}

// MARK: settings

func settingsTests() {
    var (s, cleanup) = makeSettings()
    check("defaults", s.startActive && s.nudgeSeconds == 0 && s.scheduleStartMin == 540 && s.scheduleEndMin == 1020
          && s.pauseBatteryPercent == 20 && !s.endLock && !s.endShutdown && s.scheduleEndTurnsOff && !s.wiggleMouse)
    s.watchApps = [WatchedApp(id: "us.zoom.xos", name: "zoom.us"), WatchedApp(id: "com.tinyspeck.slackmacgap", name: "Slack")]
    eq("watched apps round trip", 2, s.watchApps.count)
    eq("watched app id", "com.tinyspeck.slackmacgap", s.watchApps[1].id)
    cleanup()

    (s, cleanup) = makeSettings { $0.set(60, forKey: "NudgeSeconds") }
    eq("v1 -> Auto nudge timing", 0, s.nudgeSeconds)
    cleanup()

    (s, cleanup) = makeSettings { d in
        d.set(2, forKey: "SettingsVersion"); d.set(12, forKey: "ScheduleStart"); d.set(22, forKey: "ScheduleEnd")
    }
    check("v2 hours -> minutes (12 PM - 10 PM)", s.scheduleStartMin == 720 && s.scheduleEndMin == 1320)
    cleanup()

    (s, cleanup) = makeSettings { d in
        d.set(3, forKey: "SettingsVersion"); d.set(5, forKey: "NudgeSeconds")
        d.set(600, forKey: "ScheduleStartMin"); d.set(600, forKey: "ScheduleEndMin"); d.set(150, forKey: "PauseBatteryPercent")
    }
    eq("invalid nudge time falls back to Auto", 0, s.nudgeSeconds)
    eq("same start and end is repaired (+8 h)", 1080, s.scheduleEndMin)
    eq("battery limit is capped", 90, s.pauseBatteryPercent)
    cleanup()

    eq("login item: Homebrew's versioned path becomes the stable one", "/opt/homebrew/opt/kapeyn/Kapeyn.app",
       LoginItem.stablePath("/opt/homebrew/Cellar/kapeyn/2.3.0/Kapeyn.app"))
    eq("login item: Intel Homebrew too", "/usr/local/opt/kapeyn/Kapeyn.app",
       LoginItem.stablePath("/usr/local/Cellar/kapeyn/2.3.0_1/Kapeyn.app"))
    eq("login item: other paths unchanged", "/Users/me/Applications/Kapeyn.app",
       LoginItem.stablePath("/Users/me/Applications/Kapeyn.app"))
}

// MARK: downloads

func netMonitorTests() {
    let fast: UInt32 = 2 * 1024 * 1024 // 2 MB per 5-second sample = ~400 KB/s
    let slow: UInt32 = 10 * 1024
    var t = t0
    var bytes: UInt32 = 0

    let n = NetMonitor()
    check("first reading is only a baseline", !n.sample(["en0": bytes], now: t))
    bytes &+= fast; t = t.addingTimeInterval(5)
    check("one burst (e.g. a sync) doesn't count", !n.sample(["en0": bytes], now: t))

    let d = NetMonitor()
    bytes = UInt32.max - 1_000_000 // near the top, so the 32-bit counter wraps during the test
    t = t0
    _ = d.sample(["en0": bytes], now: t)
    var busy = false
    for _ in 0..<3 { bytes &+= fast; t = t.addingTimeInterval(5); busy = d.sample(["en0": bytes], now: t) }
    check("sustained traffic counts as downloading (even across a counter wrap)", busy)
    for _ in 0..<10 { bytes &+= slow; t = t.addingTimeInterval(5); busy = d.sample(["en0": bytes], now: t) }
    check("stays busy for a minute after it stops", busy)
    for _ in 0..<4 { bytes &+= slow; t = t.addingTimeInterval(5); busy = d.sample(["en0": bytes], now: t) }
    check("then stops", !busy)

    let r = NetMonitor()
    _ = r.sample(["en0": 5_000_000], now: t0)
    check("a counter reset alone isn't a download", !r.sample(["en0": 1000], now: t0.addingTimeInterval(5)))
}

// MARK: doctor

func doctorTests() {
    func f(_ seconds: Int, _ input: Bool) -> Finding {
        return Finding(what: "x", seconds: seconds, inputBased: input, byOrg: false, kind: input ? "saver" : "sleep")
    }
    eq("nothing found: 2 min", 120, Doctor.recommendedNudgeSeconds([]))
    eq("5-minute lock: 2 min 30 s", 150, Doctor.recommendedNudgeSeconds([f(300, true)]))
    eq("very short lock: never below 30 s", 30, Doctor.recommendedNudgeSeconds([f(40, true)]))
    eq("long lock: capped at 4 min", 240, Doctor.recommendedNudgeSeconds([f(3600, true)]))
    eq("only power timers: 2 min", 120, Doctor.recommendedNudgeSeconds([f(60, false)]))
    eq("uses the shortest input-based timer", 180, Doctor.recommendedNudgeSeconds([f(900, true), f(360, true), f(60, false)]))
}

// MARK: help

func helpTests() {
    let path = repoRoot + "/help/help.md"
    guard let text = try? String(contentsOfFile: path, encoding: .utf8) else {
        check("help.md can be read at \(path)", false)
        return
    }
    let topics = HelpContent.parse(text)
    eq("11 topics", 11, topics.count)
    check("topic titles are unique", Set(topics.map { $0.title }).count == topics.count)
    check("every topic has text", topics.allSatisfy { $0.lines.contains { !$0.trimmingCharacters(in: .whitespaces).isEmpty } })
    let all = topics.map { ([$0.title] + $0.lines).joined(separator: "\n") }.joined(separator: "\n")
    check("no unfilled {tokens}", !all.contains("{"))
    check("no [win]/[mac] markers or comments left", !all.contains("[win]") && !all.contains("[mac]") && !all.contains("%%"))
    check("no Windows-only text on a Mac", !all.contains("Ctrl+Alt+K") && !all.contains("Activity method") && !all.contains("install.ps1"))
    check("Mac wording is used", all.contains("⌃⌥K") && all.contains("Start at login") && all.contains("Control-click"))
}
