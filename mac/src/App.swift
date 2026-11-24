import AppKit
import ApplicationServices

/// One reason to stay awake. `key` identifies it (for snoozing), `label` is what the user sees.
struct Reason {
    let key: String
    let label: String
}

final class AppDelegate: NSObject, NSApplicationDelegate, NSMenuDelegate {
    let settings = Settings()
    let power = Power()
    let net = NetMonitor()
    let hotKey = HotKey()
    let ipc = IPCServer()
    let durations = [15, 30, 60, 120, 240, 480]
    let nudgeChoices = [30, 60, 120, 240]
    let batteryChoices = [10, 20, 30]
    let dayNames = ["Sunday", "Monday", "Tuesday", "Wednesday", "Thursday", "Friday", "Saturday"]
    let hotkeyText = "⌃⌥K"
    var cliLaunch = false

    var statusItem: NSStatusItem!
    let menu = NSMenu()
    var timer: Timer?
    let iconOn = IconArt.menuBarIcon(on: true)
    let iconOff = IconArt.menuBarIcon(on: false)

    // MARK: state
    var manualOn = false
    var manualUntil: Date?
    var holds: [Int32: String] = [:]      // kapeyn run: pid -> label
    var snoozed = Set<String>()           // automatic reasons the user turned off
    var lastAuto: [Reason] = []
    var reasons: [Reason] = []
    var awake = false
    var paused = false
    var pauseWhy = ""
    var locked = false
    var wasInWorkHours = false
    var batteryOverride = false
    var hotKeyOK = true
    var lastNudge = Date.distantPast
    var lastEvaluate = Date.distantPast
    var autoNudgeSeconds = 120
    var lastPolicyScan = Date.distantPast
    var shutdownAlert: NSAlert?
    var shutdownRemaining = 0

    // MARK: menu items
    var statusLine: NSMenuItem!
    var detailLine: NSMenuItem!
    var toggleItem: NSMenuItem!
    let durationMenu = NSMenu()
    var callItem: NSMenuItem!
    var netItem: NSMenuItem!
    var appsItem: NSMenuItem!
    let appsMenu = NSMenu()
    var scheduleItem: NSMenuItem!
    var screenItem: NSMenuItem!
    var activityItem: NSMenuItem!
    var mouseItem: NSMenuItem!
    var intervalItem: NSMenuItem!
    let intervalMenu = NSMenu()
    var lockedItem: NSMenuItem!
    let batteryMenu = NSMenu()
    var hotkeyItem: NSMenuItem!
    var startActiveItem: NSMenuItem!
    var loginItem: NSMenuItem!

    var effectiveNudgeSeconds: Int { return settings.nudgeSeconds > 0 ? settings.nudgeSeconds : autoNudgeSeconds }

    func applicationDidFinishLaunching(_ notification: Notification) {
        // Only one copy: if another is running, poke it (it turns on and says hello) and quit.
        let others = NSRunningApplication.runningApplications(withBundleIdentifier: kBundleID)
            .filter { $0.processIdentifier != ProcessInfo.processInfo.processIdentifier }
        if !others.isEmpty {
            if !cliLaunch {
                DistributedNotificationCenter.default().postNotificationName(
                    Notification.Name("com.kapeyn.Kapeyn.secondLaunch"), object: nil, userInfo: nil, deliverImmediately: true)
            }
            NSApp.terminate(nil)
            return
        }

        let dnc = DistributedNotificationCenter.default()
        dnc.addObserver(self, selector: #selector(onSecondLaunch),
                        name: Notification.Name("com.kapeyn.Kapeyn.secondLaunch"), object: nil)
        dnc.addObserver(self, selector: #selector(screenLocked),
                        name: Notification.Name("com.apple.screenIsLocked"), object: nil)
        dnc.addObserver(self, selector: #selector(screenUnlocked),
                        name: Notification.Name("com.apple.screenIsUnlocked"), object: nil)
        NSWorkspace.shared.notificationCenter.addObserver(self, selector: #selector(didWake),
                                                          name: NSWorkspace.didWakeNotification, object: nil)

        statusItem = NSStatusBar.system.statusItem(withLength: NSStatusItem.squareLength)
        if let button = statusItem.button {
            button.image = iconOff
            button.target = self
            button.action = #selector(statusClicked(_:))
            button.sendAction(on: [.leftMouseUp, .rightMouseUp])
        }
        buildMenu()

        ipcHandler = { [weak self] text in self?.handleCommand(text) ?? "error: Kapeyn is quitting" }
        ipc.start()

        hotKeyAction = { [weak self] in self?.toggle() }
        applyHotkey()

        let t = Timer(timeInterval: 5, target: self, selector: #selector(tick), userInfo: nil, repeats: true)
        t.tolerance = 1
        RunLoop.main.add(t, forMode: .common)
        timer = t

        LoginItem.repairPath()
        rescanPolicies()
        wasInWorkHours = Triggers.inWorkHours(settings)
        if settings.startActive && !cliLaunch { manualOn = true }
        evaluate(allowNudge: false)

        if !settings.welcomed && !cliLaunch {
            settings.welcomed = true
            showAlert("Kapeyn is running",
                      "Your Mac will stay awake. Look for the cup in the menu bar:\n\n"
                      + "• Click it to turn Kapeyn on or off (or press \(hotkeyText) anywhere).\n"
                      + "• Right-click (or Control-click) it for options, including automatic modes and the lock doctor.")
        }
    }

    func applicationWillTerminate(_ notification: Notification) {
        power.releaseAll()
    }

    // MARK: engine

    @objc func tick() { evaluate(allowNudge: true) }

    func evaluate(allowNudge: Bool) {
        let now = Date()
        if now.timeIntervalSince(lastPolicyScan) >= 600 { rescanPolicies() }

        if manualOn, let u = manualUntil, now >= u {
            manualOn = false
            manualUntil = nil
        }

        // Work hours. wasInWorkHours tracks the window itself (not whether the schedule is on), so switching
        // the schedule off never counts as "work hours ended". Only react to an end seen live: if we were
        // asleep across it (no evaluation in the last 2 minutes), don't lock or shut down later.
        let fresh = now.timeIntervalSince(lastEvaluate) < 120
        lastEvaluate = now
        let inWindow = Triggers.inWorkHours(settings, now)
        if settings.scheduleEnabled && wasInWorkHours && !inWindow && fresh { workHoursEnded() }
        wasInWorkHours = inWindow
        let inHours = settings.scheduleEnabled && inWindow

        // Automatic reasons
        var auto: [Reason] = []
        if inHours { auto.append(Reason(key: "hours", label: "work hours")) }
        if settings.whileCall && Triggers.micInUse() { auto.append(Reason(key: "call", label: "on a call")) }
        if settings.whileDownloading && net.update() { auto.append(Reason(key: "net", label: "downloading")) }
        for app in settings.watchApps where Triggers.isRunning(app.id) {
            auto.append(Reason(key: "app:" + app.id, label: app.name + " is open"))
        }
        for (pid, label) in holds {
            if Triggers.isAlive(pid) { auto.append(Reason(key: "cli:\(pid)", label: "running " + label)) }
            else { holds[pid] = nil }
        }
        lastAuto = auto

        // Forget snoozes for reasons that have ended, so the next call/download wakes Kapeyn again.
        let keys = Set(auto.map { $0.key })
        snoozed = snoozed.filter { keys.contains($0) }

        var active: [Reason] = []
        if manualOn {
            active.append(Reason(key: "manual", label: manualUntil.map { Format.remaining($0.timeIntervalSince(now)) + " left" } ?? "turned on"))
        }
        active += auto.filter { !snoozed.contains($0.key) }
        reasons = active
        let wanted = !active.isEmpty

        // Low battery pause
        paused = false
        pauseWhy = ""
        let battery = Triggers.battery()
        if !battery.onBattery { batteryOverride = false }
        if wanted && settings.pauseBatteryPercent > 0 && battery.onBattery && battery.percent >= 0
            && battery.percent < settings.pauseBatteryPercent && !batteryOverride {
            paused = true
            pauseWhy = "battery at \(battery.percent)%"
        }

        awake = wanted && !paused
        let screenMayGoOff = locked && settings.screenOffWhenLocked
        power.set(awake: awake, display: settings.keepScreenOn && !screenMayGoOff)

        if awake && allowNudge && settings.simulateActivity && !locked {
            let n = Double(effectiveNudgeSeconds - 2)
            if Power.idleSeconds() >= n && now.timeIntervalSince(lastNudge) >= n {
                power.declareUserActivity()
                if settings.wiggleMouse && AXIsProcessTrusted() { Power.wiggleMouse() }
                lastNudge = now
            }
        }
        updateUI()
    }

    func workHoursEnded() {
        guard settings.scheduleEndTurnsOff else { return }
        manualOn = false
        manualUntil = nil
        if settings.endShutdown {
            DispatchQueue.main.async { self.shutdownCountdown() }
        } else if settings.endLock && !locked {
            SystemActions.lockScreen()
        }
    }

    func shutdownCountdown() {
        guard shutdownAlert == nil else { return }
        let alert = NSAlert()
        alert.messageText = "Work hours are over."
        alert.addButton(withTitle: "Shut Down Now")
        alert.addButton(withTitle: "Cancel")
        shutdownAlert = alert
        shutdownRemaining = 60
        updateShutdownText()
        let t = Timer(timeInterval: 1, target: self, selector: #selector(shutdownTick), userInfo: nil, repeats: true)
        RunLoop.main.add(t, forMode: .modalPanel)

        NSApp.activate(ignoringOtherApps: true)
        let result = alert.runModal()
        t.invalidate()
        shutdownAlert = nil
        if result == .alertFirstButtonReturn, let error = SystemActions.shutDown() {
            showAlert("Kapeyn couldn't shut down your Mac",
                      "macOS said: \(error)\n\nAllow Kapeyn to control System Events in System Settings > Privacy & Security > Automation.")
        }
    }

    @objc func shutdownTick() {
        shutdownRemaining -= 1
        updateShutdownText()
        if shutdownRemaining <= 0 { NSApp.stopModal(withCode: .alertFirstButtonReturn) }
    }

    func updateShutdownText() {
        shutdownAlert?.informativeText = "Kapeyn will shut down this Mac in \(max(0, shutdownRemaining)) seconds.\n"
            + "Save your work, or click Cancel to keep working."
    }

    func rescanPolicies() {
        lastPolicyScan = Date()
        autoNudgeSeconds = Doctor.recommendedNudgeSeconds(Doctor.scan())
    }

    func turnOn(_ minutes: Int?) {
        manualOn = true
        manualUntil = minutes.map { Date().addingTimeInterval(Double($0) * 60) }
        snoozed.removeAll()
        batteryOverride = batteryOverride || paused
        lastNudge = .distantPast
        evaluate(allowNudge: false)
    }

    func turnOff() {
        manualOn = false
        manualUntil = nil
        for r in lastAuto { snoozed.insert(r.key) }
        batteryOverride = false
        evaluate(allowNudge: false)
    }

    func toggle() {
        if awake { turnOff() } else { turnOn(nil) }
    }

    // MARK: events

    @objc func didWake() {
        // Start fresh, so an end of work hours that passed while asleep isn't acted on.
        wasInWorkHours = Triggers.inWorkHours(settings)
        evaluate(allowNudge: false)
    }
    @objc func screenLocked() { locked = true; evaluate(allowNudge: false) }
    @objc func screenUnlocked() { locked = false; evaluate(allowNudge: false) }

    @objc func onSecondLaunch() {
        if !awake { turnOn(nil) }
        showAlert("Kapeyn is already running", "Your Mac is being kept awake. Look for the cup in the menu bar.")
    }

    /// Commands from the "kapeyn" command line tool.
    func handleCommand(_ line: String) -> String {
        let parts = line.trimmingCharacters(in: .whitespacesAndNewlines)
            .split(separator: " ", maxSplits: 2, omittingEmptySubsequences: true).map(String.init)
        let verb = parts.first?.lowercased() ?? "status"
        switch verb {
        case "ping":
            return "pong"
        case "on":
            if parts.count > 1, let n = Int(parts[1]), n > 0 { turnOn(n) } else { turnOn(nil) }
            return "Kapeyn: " + statusText()
        case "off":
            turnOff()
            return "Kapeyn: " + statusText()
        case "toggle":
            toggle()
            return "Kapeyn: " + statusText()
        case "status":
            evaluate(allowNudge: false)
            return statusReport()
        case "hold":
            guard parts.count > 1, let pid = Int32(parts[1]) else { return "error: expected hold <pid> <label>" }
            holds[pid] = parts.count > 2 ? parts[2] : "a command"
            snoozed.remove("cli:\(pid)")
            evaluate(allowNudge: false)
            return "ok"
        case "release":
            guard parts.count > 1, let pid = Int32(parts[1]) else { return "error: expected release <pid>" }
            holds[pid] = nil
            evaluate(allowNudge: false)
            return "ok"
        case "doctor":
            return doctorReport()
        default:
            return "error: unknown command \"\(verb)\""
        }
    }

    // MARK: text

    func statusText() -> String {
        if paused { return "Paused - " + pauseWhy }
        if !awake { return "Off - Mac may sleep" }
        if reasons.count == 1 && reasons[0].key == "manual" && manualUntil == nil { return "Awake" }
        return "Awake - " + reasons.map { $0.label }.joined(separator: ", ")
    }

    func detailText() -> String? {
        guard awake else { return nil }
        if !settings.simulateActivity { return "Screen lock prevention is off" }
        if locked { return "Screen is locked - not nudging" }
        return "Last nudge \(Format.ago(lastNudge)) - idle \(Format.duration(Int(Power.idleSeconds())))"
    }

    func statusReport() -> String {
        var lines = ["Kapeyn: " + statusText()]
        if let d = detailText() { lines.append("  " + d) }
        let lockText = settings.simulateActivity
            ? "yes, nudge after \(Format.duration(effectiveNudgeSeconds)) idle" + (settings.nudgeSeconds == 0 ? " (auto)" : "")
            : "no"
        lines.append("  Keep screen on: \(yesNo(settings.keepScreenOn)) | Prevent screen lock: \(lockText)")
        var auto = "  Automatic: calls \(yesNo(settings.whileCall)) | downloads \(yesNo(settings.whileDownloading)) | work hours "
        auto += settings.scheduleEnabled ? Format.schedule(settings) : "no"
        let apps = settings.watchApps
        if !apps.isEmpty { auto += " | apps " + apps.map { $0.name }.joined(separator: ", ") }
        lines.append(auto)
        return lines.joined(separator: "\n")
    }

    func yesNo(_ b: Bool) -> String { return b ? "yes" : "no" }

    func doctorReport() -> String {
        evaluate(allowNudge: false)
        return Doctor.report(settings: settings, power: power, awake: awake, status: statusText(),
                             nudgeSeconds: effectiveNudgeSeconds, autoNudge: settings.nudgeSeconds == 0)
    }

    func updateUI() {
        statusItem?.button?.image = awake ? iconOn : iconOff
        statusItem?.button?.toolTip = "Kapeyn: " + statusText()
    }

    func applyHotkey() {
        hotKey.unregister()
        hotKeyOK = !settings.hotkey || hotKey.register()
    }

    func showAlert(_ title: String, _ text: String) {
        NSApp.activate(ignoringOtherApps: true)
        let alert = NSAlert()
        alert.messageText = title
        alert.informativeText = text
        alert.alertStyle = .informational
        alert.runModal()
    }

    /// A long, monospaced, scrollable text with Close / Copy buttons.
    func showReport(_ title: String, _ text: String, extraButton: String? = nil) -> Bool {
        NSApp.activate(ignoringOtherApps: true)
        let alert = NSAlert()
        alert.messageText = title
        let scroll = NSScrollView(frame: NSRect(x: 0, y: 0, width: 620, height: 380))
        scroll.hasVerticalScroller = true
        scroll.borderType = .bezelBorder
        let textView = NSTextView(frame: scroll.bounds)
        textView.isEditable = false
        textView.font = NSFont.monospacedSystemFont(ofSize: 11, weight: .regular)
        textView.string = text
        textView.autoresizingMask = [.width]
        scroll.documentView = textView
        alert.accessoryView = scroll
        alert.addButton(withTitle: "Close")
        alert.addButton(withTitle: "Copy")
        if let extra = extraButton { alert.addButton(withTitle: extra) }
        let result = alert.runModal()
        if result == .alertSecondButtonReturn {
            NSPasteboard.general.clearContents()
            NSPasteboard.general.setString(text, forType: .string)
        }
        return result == .alertThirdButtonReturn
    }

    // MARK: menu

    @objc func statusClicked(_ sender: Any?) {
        let event = NSApp.currentEvent
        let wantsMenu = event?.type == .rightMouseUp
            || event?.modifierFlags.contains(.control) == true
            || event?.modifierFlags.contains(.option) == true
        if wantsMenu {
            statusItem.menu = menu
            statusItem.button?.performClick(nil) // opens the menu
            statusItem.menu = nil                // so the next left-click toggles again
        } else {
            toggle()
        }
    }

    func item(_ title: String, _ action: Selector?, key: String = "", tag: Int = 0) -> NSMenuItem {
        let i = NSMenuItem(title: title, action: action, keyEquivalent: key)
        i.target = self
        i.tag = tag
        return i
    }

    func submenu(_ title: String, _ sub: NSMenu) -> NSMenuItem {
        let i = NSMenuItem(title: title, action: nil, keyEquivalent: "")
        sub.autoenablesItems = false
        i.submenu = sub
        return i
    }

    func buildMenu() {
        menu.delegate = self
        menu.autoenablesItems = false

        statusLine = item("Kapeyn", nil)
        statusLine.isEnabled = false
        detailLine = item("", nil)
        detailLine.isEnabled = false
        menu.addItem(statusLine)
        menu.addItem(detailLine)
        menu.addItem(.separator())

        toggleItem = item("Turn off", #selector(toggleClicked))
        menu.addItem(toggleItem)

        durationMenu.addItem(item("Until I turn it off", #selector(pickDuration(_:)), tag: 0))
        durationMenu.addItem(.separator())
        for m in durations { durationMenu.addItem(item(Format.minutes(m), #selector(pickDuration(_:)), tag: m)) }
        menu.addItem(submenu("Keep awake for", durationMenu))
        menu.addItem(.separator())

        // Automatic
        let autoMenu = NSMenu()
        callItem = item("While I'm on a call (microphone in use)", #selector(toggleCall))
        netItem = item("While downloading or streaming", #selector(toggleNet))
        appsMenu.delegate = self
        appsItem = submenu("While these apps are open", appsMenu)
        autoMenu.addItem(callItem)
        autoMenu.addItem(netItem)
        autoMenu.addItem(appsItem)
        autoMenu.addItem(.separator())
        scheduleItem = item("During work hours", #selector(toggleSchedule))
        autoMenu.addItem(scheduleItem)
        autoMenu.addItem(item("Set work hours…", #selector(showWorkHours)))
        menu.addItem(submenu("Keep awake automatically", autoMenu))

        // Settings
        let settingsMenu = NSMenu()
        screenItem = item("Keep screen on", #selector(toggleScreen))
        activityItem = item("Prevent screen lock & Away status", #selector(toggleActivity))
        activityItem.toolTip = "When you're idle, Kapeyn tells macOS you're active so the screen saver and lock don't kick in."
        mouseItem = item("Also nudge the mouse (Teams/Slack)", #selector(toggleMouse))
        mouseItem.toolTip = "Moves the pointer 1 pixel and back while you're idle. Needs Accessibility permission."
        intervalMenu.addItem(item("Auto (recommended)", #selector(pickInterval(_:)), tag: 0))
        intervalMenu.addItem(.separator())
        for s in nudgeChoices { intervalMenu.addItem(item(Format.duration(s), #selector(pickInterval(_:)), tag: s)) }
        intervalItem = submenu("Nudge when idle for", intervalMenu)
        lockedItem = item("Let the screen sleep when I lock the Mac", #selector(toggleLocked))
        batteryMenu.addItem(item("Never", #selector(pickBattery(_:)), tag: 0))
        for p in batteryChoices { batteryMenu.addItem(item("Below \(p)%", #selector(pickBattery(_:)), tag: p)) }
        hotkeyItem = item("Shortcut \(hotkeyText) turns Kapeyn on/off", #selector(toggleHotkey))
        startActiveItem = item("Turn on when Kapeyn starts", #selector(toggleStartActive))
        loginItem = item("Start at login", #selector(toggleLogin))

        settingsMenu.addItem(screenItem)
        settingsMenu.addItem(activityItem)
        settingsMenu.addItem(mouseItem)
        settingsMenu.addItem(intervalItem)
        settingsMenu.addItem(.separator())
        settingsMenu.addItem(lockedItem)
        settingsMenu.addItem(submenu("Pause on low battery", batteryMenu))
        settingsMenu.addItem(.separator())
        settingsMenu.addItem(hotkeyItem)
        settingsMenu.addItem(startActiveItem)
        settingsMenu.addItem(loginItem)
        menu.addItem(submenu("Settings", settingsMenu))
        menu.addItem(.separator())

        menu.addItem(item("Lock doctor: what locks my Mac?", #selector(showDoctor)))
        menu.addItem(item("Command line…", #selector(showCommandLine)))
        menu.addItem(.separator())
        menu.addItem(item("Quit Kapeyn", #selector(quit), key: "q"))
    }

    func menuNeedsUpdate(_ m: NSMenu) {
        if m === appsMenu { populateApps(); return }
        guard m === menu else { return }

        evaluate(allowNudge: false)
        statusLine.title = "Kapeyn: " + statusText()
        let detail = detailText()
        detailLine.title = detail ?? ""
        detailLine.isHidden = detail == nil
        toggleItem.title = (awake ? "Turn off" : "Turn on") + (settings.hotkey && hotKeyOK ? "   \(hotkeyText)" : "")

        callItem.state = settings.whileCall ? .on : .off
        netItem.state = settings.whileDownloading ? .on : .off
        let count = settings.watchApps.count
        appsItem.title = count == 0 ? "While these apps are open" : "While these apps are open (\(count))"
        scheduleItem.state = settings.scheduleEnabled ? .on : .off
        scheduleItem.title = "During work hours (\(Format.schedule(settings)))"

        screenItem.state = settings.keepScreenOn ? .on : .off
        activityItem.state = settings.simulateActivity ? .on : .off
        mouseItem.state = settings.wiggleMouse ? .on : .off
        mouseItem.title = settings.wiggleMouse && !AXIsProcessTrusted()
            ? "Also nudge the mouse (needs permission)"
            : "Also nudge the mouse (Teams/Slack)"
        mouseItem.isEnabled = settings.simulateActivity
        intervalItem.isEnabled = settings.simulateActivity
        for i in intervalMenu.items where !i.isSeparatorItem {
            i.state = i.tag == settings.nudgeSeconds ? .on : .off
            if i.tag == 0 { i.title = "Auto (recommended: \(Format.duration(autoNudgeSeconds)))" }
        }
        lockedItem.state = settings.screenOffWhenLocked ? .on : .off
        for i in batteryMenu.items { i.state = i.tag == settings.pauseBatteryPercent ? .on : .off }
        hotkeyItem.state = settings.hotkey ? .on : .off
        hotkeyItem.title = "Shortcut \(hotkeyText) turns Kapeyn on/off" + (settings.hotkey && !hotKeyOK ? " (taken by another app)" : "")
        startActiveItem.state = settings.startActive ? .on : .off
        loginItem.state = LoginItem.isEnabled ? .on : .off
    }

    func populateApps() {
        appsMenu.removeAllItems()
        let watched = settings.watchApps
        if watched.isEmpty {
            let none = item("None yet - add one below", nil)
            none.isEnabled = false
            appsMenu.addItem(none)
        }
        for (index, app) in watched.enumerated() {
            let i = item(app.name, #selector(removeApp(_:)), tag: index)
            i.state = .on
            i.toolTip = "Click to stop watching \(app.name)"
            appsMenu.addItem(i)
        }
        appsMenu.addItem(.separator())

        let addMenu = NSMenu()
        addMenu.autoenablesItems = false
        let ids = Set(watched.map { $0.id })
        for app in Triggers.runningApps() where !ids.contains(app.id) {
            let i = item(app.name, #selector(addApp(_:)))
            i.representedObject = app.id
            addMenu.addItem(i)
        }
        if addMenu.items.isEmpty {
            let none = item("No other open apps", nil)
            none.isEnabled = false
            addMenu.addItem(none)
        }
        appsMenu.addItem(submenu("Add an open app", addMenu))
    }

    // MARK: actions

    func changed() { evaluate(allowNudge: false) }

    @objc func toggleClicked() { toggle() }
    @objc func pickDuration(_ sender: NSMenuItem) { turnOn(sender.tag == 0 ? nil : sender.tag) }
    @objc func toggleCall() { settings.whileCall.toggle(); changed() }
    @objc func toggleNet() { settings.whileDownloading.toggle(); changed() }
    @objc func toggleSchedule() { settings.scheduleEnabled.toggle(); changed() }
    @objc func showWorkHours() {
        guard WorkHoursPanel(settings: settings).run() else { return }
        // New times: start from where we are now, so saving never counts as "work hours just ended".
        wasInWorkHours = Triggers.inWorkHours(settings)
        changed()
    }

    @objc func removeApp(_ sender: NSMenuItem) {
        var apps = settings.watchApps
        if apps.indices.contains(sender.tag) { apps.remove(at: sender.tag) }
        settings.watchApps = apps
        changed()
    }

    @objc func addApp(_ sender: NSMenuItem) {
        guard let id = sender.representedObject as? String else { return }
        settings.watchApps = settings.watchApps + [WatchedApp(id: id, name: sender.title)]
        changed()
    }

    @objc func toggleScreen() { settings.keepScreenOn.toggle(); changed() }
    @objc func toggleActivity() { settings.simulateActivity.toggle(); changed() }

    @objc func toggleMouse() {
        settings.wiggleMouse.toggle()
        if settings.wiggleMouse && !AXIsProcessTrusted() {
            // Shows the system prompt that opens Privacy & Security > Accessibility.
            _ = AXIsProcessTrustedWithOptions(["AXTrustedCheckOptionPrompt": true] as CFDictionary)
        }
    }

    @objc func pickInterval(_ sender: NSMenuItem) { settings.nudgeSeconds = sender.tag; changed() }
    @objc func toggleLocked() { settings.screenOffWhenLocked.toggle(); changed() }
    @objc func pickBattery(_ sender: NSMenuItem) { settings.pauseBatteryPercent = sender.tag; changed() }
    @objc func toggleHotkey() { settings.hotkey.toggle(); applyHotkey() }
    @objc func toggleStartActive() { settings.startActive.toggle() }

    @objc func toggleLogin() {
        do {
            try LoginItem.set(!LoginItem.isEnabled)
        } catch {
            showAlert("Couldn't change the login setting", error.localizedDescription)
        }
    }

    @objc func showDoctor() {
        _ = showReport("Kapeyn Lock Doctor", doctorReport())
    }

    @objc func showCommandLine() {
        let exe = Bundle.main.executablePath ?? "/Applications/Kapeyn.app/Contents/MacOS/Kapeyn"
        let installed = FileManager.default.fileExists(atPath: "/usr/local/bin/kapeyn")
        let text = "Control Kapeyn from Terminal:\n\n" + CLI.help + "\n\n"
            + (installed
                ? "The kapeyn command is installed at /usr/local/bin/kapeyn."
                : "To get the kapeyn command, click \"Install command\" (asks for your password once),\n"
                  + "or add this line to ~/.zshrc:\n\n  alias kapeyn=\"\(exe)\"")
        if showReport("Kapeyn command line", text, extraButton: installed ? nil : "Install command") {
            installCommand(exe)
        }
    }

    func installCommand(_ exe: String) {
        let quoted = exe.replacingOccurrences(of: "'", with: "'\\''")
        let source = "do shell script \"mkdir -p /usr/local/bin && ln -sf '\(quoted)' /usr/local/bin/kapeyn\" with administrator privileges"
        var error: NSDictionary?
        NSAppleScript(source: source)?.executeAndReturnError(&error)
        if error == nil {
            showAlert("The kapeyn command is installed", "Open a new Terminal window and try:  kapeyn status")
        }
    }

    @objc func quit() { NSApp.terminate(nil) }
}
