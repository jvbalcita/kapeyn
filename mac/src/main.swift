// Kapeyn for macOS - keeps your Mac awake.
// Single-file AppKit menu bar app. Build/install with: bash install.sh  (needs the free Xcode Command Line Tools)
//
// How it keeps the Mac awake (same idea as the Windows version):
//   1. Power assertion  - IOKit "PreventUserIdleDisplaySleep" (or "PreventUserIdleSystemSleep" if the screen may
//                         turn off). Check it with:  pmset -g assertions | grep Kapeyn
//   2. Activity signal  - when you've been idle a while, IOPMAssertionDeclareUserActivity tells macOS the user is
//                         active. That resets the idle timer behind the screen saver / screen lock. No permissions.
//   3. Mouse nudge (opt-in) - posts a 1px mouse move and puts the pointer back, so apps that watch raw input
//                         (Teams/Slack "Away") see activity too. Needs Accessibility permission.

import AppKit
import ApplicationServices
import IOKit.pwr_mgt

let kBundleID = "com.kapeyn.Kapeyn"
let kSecondLaunch = Notification.Name("com.kapeyn.Kapeyn.secondLaunch")

// MARK: - Settings (UserDefaults: com.kapeyn.Kapeyn)

final class Settings {
    private let d = UserDefaults.standard

    init() {
        d.register(defaults: [
            "StartActive": true,
            "KeepScreenOn": true,
            "SimulateActivity": true,
            "WiggleMouse": false,
            "NudgeSeconds": 60,
            "Welcomed": false,
        ])
    }

    var startActive: Bool {
        get { return d.bool(forKey: "StartActive") }
        set { d.set(newValue, forKey: "StartActive") }
    }
    var keepScreenOn: Bool {
        get { return d.bool(forKey: "KeepScreenOn") }
        set { d.set(newValue, forKey: "KeepScreenOn") }
    }
    var simulateActivity: Bool {
        get { return d.bool(forKey: "SimulateActivity") }
        set { d.set(newValue, forKey: "SimulateActivity") }
    }
    var wiggleMouse: Bool {
        get { return d.bool(forKey: "WiggleMouse") }
        set { d.set(newValue, forKey: "WiggleMouse") }
    }
    var nudgeSeconds: Int {
        get {
            let n = d.integer(forKey: "NudgeSeconds")
            return (10...3600).contains(n) ? n : 60
        }
        set { d.set(newValue, forKey: "NudgeSeconds") }
    }
    var welcomed: Bool {
        get { return d.bool(forKey: "Welcomed") }
        set { d.set(newValue, forKey: "Welcomed") }
    }
}

// MARK: - Power + activity

final class Power {
    private var sleepAssertion: IOPMAssertionID = 0
    private var activityAssertion: IOPMAssertionID = 0
    private var napBlocker: NSObjectProtocol?

    func hold(keepScreenOn: Bool) {
        releaseSleepAssertion()
        let type = keepScreenOn ? "PreventUserIdleDisplaySleep" : "PreventUserIdleSystemSleep"
        IOPMAssertionCreateWithName(type as CFString,
                                    IOPMAssertionLevel(kIOPMAssertionLevelOn),
                                    "Kapeyn is keeping your Mac awake" as CFString,
                                    &sleepAssertion)
        if napBlocker == nil {
            // Keep our timer running on schedule (no App Nap) while active.
            napBlocker = ProcessInfo.processInfo.beginActivity(options: .userInitiatedAllowingIdleSystemSleep,
                                                               reason: "Kapeyn keep-awake timer")
        }
    }

    func releaseAll() {
        releaseSleepAssertion()
        if activityAssertion != 0 {
            IOPMAssertionRelease(activityAssertion)
            activityAssertion = 0
        }
        if let n = napBlocker {
            ProcessInfo.processInfo.endActivity(n)
            napBlocker = nil
        }
    }

    private func releaseSleepAssertion() {
        if sleepAssertion != 0 {
            IOPMAssertionRelease(sleepAssertion)
            sleepAssertion = 0
        }
    }

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

// MARK: - Start at login (~/Library/LaunchAgents, no admin needed)

enum LoginItem {
    static let label = "com.kapeyn.Kapeyn.login"

    static var plistURL: URL {
        return FileManager.default.homeDirectoryForCurrentUser
            .appendingPathComponent("Library/LaunchAgents/\(label).plist")
    }

    static var isEnabled: Bool {
        return FileManager.default.fileExists(atPath: plistURL.path)
    }

    static func set(_ enabled: Bool) throws {
        if enabled {
            let dict: [String: Any] = [
                "Label": label,
                "ProgramArguments": ["/usr/bin/open", "-a", Bundle.main.bundlePath],
                "RunAtLoad": true,
            ]
            try FileManager.default.createDirectory(at: plistURL.deletingLastPathComponent(),
                                                    withIntermediateDirectories: true)
            let data = try PropertyListSerialization.data(fromPropertyList: dict, format: .xml, options: 0)
            try data.write(to: plistURL, options: .atomic)
        } else if isEnabled {
            try FileManager.default.removeItem(at: plistURL)
        }
    }

    /// If the app was moved, point the login item at the new location.
    static func repairPath() {
        guard isEnabled,
              let data = try? Data(contentsOf: plistURL),
              let plist = try? PropertyListSerialization.propertyList(from: data, options: [], format: nil) as? [String: Any],
              let args = plist["ProgramArguments"] as? [String]
        else { return }
        if args.last != Bundle.main.bundlePath { try? set(true) }
    }
}

// MARK: - Icon drawing (coffee cup; steaming = awake, outline = off)

enum IconArt {
    static let amber = NSColor(srgbRed: 222 / 255, green: 128 / 255, blue: 44 / 255, alpha: 1)

    /// Cup artwork, designed on a 32x32 grid (y down).
    static func cup(size: CGFloat, on: Bool, color: NSColor) -> NSImage {
        return NSImage(size: NSSize(width: size, height: size), flipped: true) { rect in
            NSGraphicsContext.saveGraphicsState()
            defer { NSGraphicsContext.restoreGraphicsState() }

            let t = NSAffineTransform()
            t.scale(by: rect.width / 32)
            t.translateX(by: 1.5, yBy: 0)
            t.concat()

            color.set()
            let stroke: CGFloat = 2.8

            // handle (right half only, so it doesn't show inside the hollow cup)
            let handle = NSBezierPath()
            handle.appendArc(withCenter: NSPoint(x: 21.75, y: 18.5), radius: 4.15,
                             startAngle: -100, endAngle: 100, clockwise: false)
            handle.lineWidth = stroke
            handle.lineCapStyle = .round
            handle.stroke()

            // body: flat top, rounded bottom
            let body = NSBezierPath()
            body.move(to: NSPoint(x: 3, y: 12))
            body.line(to: NSPoint(x: 21, y: 12))
            body.line(to: NSPoint(x: 21, y: 22))
            body.appendArc(withCenter: NSPoint(x: 16, y: 22), radius: 5, startAngle: 0, endAngle: 90, clockwise: false)
            body.line(to: NSPoint(x: 8, y: 27))
            body.appendArc(withCenter: NSPoint(x: 8, y: 22), radius: 5, startAngle: 90, endAngle: 180, clockwise: false)
            body.close()
            if on {
                body.fill()
            } else {
                body.lineWidth = stroke
                body.lineJoinStyle = .round
                body.stroke()
            }

            // saucer
            let saucer = NSBezierPath()
            saucer.move(to: NSPoint(x: 1.5, y: 29.5))
            saucer.line(to: NSPoint(x: 23.5, y: 29.5))
            saucer.lineWidth = stroke
            saucer.lineCapStyle = .round
            saucer.stroke()

            // steam
            if on {
                for i in 0..<3 {
                    let x = 7 + CGFloat(i) * 5
                    let steam = NSBezierPath()
                    steam.move(to: NSPoint(x: x, y: 9.5))
                    steam.curve(to: NSPoint(x: x, y: 2),
                                controlPoint1: NSPoint(x: x - 2.2, y: 7),
                                controlPoint2: NSPoint(x: x + 2.2, y: 4.5))
                    steam.lineWidth = stroke * 0.8
                    steam.lineCapStyle = .round
                    steam.stroke()
                }
            }
            return true
        }
    }

    /// Monochrome template image so it matches the light/dark menu bar automatically.
    static func menuBarIcon(on: Bool) -> NSImage {
        let img = cup(size: 18, on: on, color: .black)
        img.isTemplate = true
        img.accessibilityDescription = on ? "Kapeyn: awake" : "Kapeyn: off"
        return img
    }

    /// App icon: amber cup on an espresso rounded square.
    static func appIconPNG(pixels: Int) -> Data? {
        guard let rep = NSBitmapImageRep(bitmapDataPlanes: nil, pixelsWide: pixels, pixelsHigh: pixels,
                                         bitsPerSample: 8, samplesPerPixel: 4, hasAlpha: true, isPlanar: false,
                                         colorSpaceName: .deviceRGB, bytesPerRow: 0, bitsPerPixel: 0)
        else { return nil }
        rep.size = NSSize(width: pixels, height: pixels)

        NSGraphicsContext.saveGraphicsState()
        NSGraphicsContext.current = NSGraphicsContext(bitmapImageRep: rep)
        let s = CGFloat(pixels)
        let inset = s * 0.1
        let tile = NSRect(x: inset, y: inset, width: s - 2 * inset, height: s - 2 * inset)
        let shape = NSBezierPath(roundedRect: tile, xRadius: tile.width * 0.225, yRadius: tile.width * 0.225)
        let top = NSColor(srgbRed: 0.33, green: 0.20, blue: 0.13, alpha: 1)
        let bottom = NSColor(srgbRed: 0.16, green: 0.09, blue: 0.06, alpha: 1)
        NSGradient(starting: top, ending: bottom)?.draw(in: shape, angle: -90)
        let c = tile.width * 0.72
        cup(size: c, on: true, color: amber)
            .draw(in: NSRect(x: (s - c) / 2, y: (s - c) / 2, width: c, height: c))
        NSGraphicsContext.restoreGraphicsState()

        return rep.representation(using: .png, properties: [:])
    }

    /// Build helper: writes a .iconset folder for `iconutil`.
    static func exportIconset(to dir: String) {
        let url = URL(fileURLWithPath: dir)
        try? FileManager.default.createDirectory(at: url, withIntermediateDirectories: true)
        for base in [16, 32, 128, 256, 512] {
            for scale in [1, 2] {
                let name = scale == 1 ? "icon_\(base)x\(base).png" : "icon_\(base)x\(base)@2x.png"
                if let data = appIconPNG(pixels: base * scale) {
                    try? data.write(to: url.appendingPathComponent(name))
                }
            }
        }
    }
}

// MARK: - App

final class AppDelegate: NSObject, NSApplicationDelegate, NSMenuDelegate {
    let settings = Settings()
    let power = Power()
    let durations = [15, 30, 60, 120, 240, 480]
    let nudgeChoices = [30, 60, 120, 240]

    var statusItem: NSStatusItem!
    let menu = NSMenu()
    var timer: Timer?
    var active = false
    var until: Date?
    var lastNudge = Date.distantPast
    let iconOn = IconArt.menuBarIcon(on: true)
    let iconOff = IconArt.menuBarIcon(on: false)

    var statusLine: NSMenuItem!
    var toggleItem: NSMenuItem!
    var durationMenu = NSMenu()
    var screenItem: NSMenuItem!
    var activityItem: NSMenuItem!
    var mouseItem: NSMenuItem!
    var intervalItem: NSMenuItem!
    var intervalMenu = NSMenu()
    var startActiveItem: NSMenuItem!
    var loginItem: NSMenuItem!

    func applicationDidFinishLaunching(_ notification: Notification) {
        // Only one copy: if another is running, poke it (it turns on and says hello) and quit.
        let others = NSRunningApplication.runningApplications(withBundleIdentifier: kBundleID)
            .filter { $0.processIdentifier != ProcessInfo.processInfo.processIdentifier }
        if !others.isEmpty {
            DistributedNotificationCenter.default().postNotificationName(kSecondLaunch, object: nil,
                                                                         userInfo: nil, deliverImmediately: true)
            NSApp.terminate(nil)
            return
        }
        DistributedNotificationCenter.default().addObserver(self, selector: #selector(onSecondLaunch),
                                                            name: kSecondLaunch, object: nil)
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

        let t = Timer(timeInterval: 5, target: self, selector: #selector(tick), userInfo: nil, repeats: true)
        t.tolerance = 1
        RunLoop.main.add(t, forMode: .common)
        timer = t

        LoginItem.repairPath()

        if settings.startActive { activate(nil) } else { updateUI() }

        if !settings.welcomed {
            settings.welcomed = true
            showAlert("Kapeyn is running",
                      "Your Mac will stay awake. Look for the cup in the menu bar:\n\n"
                      + "• Click it to turn Kapeyn on or off.\n"
                      + "• Right-click (or Control-click) it for options.")
        }
    }

    func applicationWillTerminate(_ notification: Notification) {
        power.releaseAll()
    }

    // MARK: state

    func activate(_ minutes: Int?) {
        active = true
        until = minutes.map { Date().addingTimeInterval(Double($0) * 60) }
        lastNudge = .distantPast
        power.hold(keepScreenOn: settings.keepScreenOn)
        updateUI()
    }

    func deactivate() {
        active = false
        until = nil
        power.releaseAll()
        updateUI()
    }

    func toggle() {
        if active { deactivate() } else { activate(nil) }
    }

    @objc func tick() {
        guard active else { return }
        if let u = until, Date() >= u {
            deactivate()
            return
        }
        if settings.simulateActivity {
            let n = Double(settings.nudgeSeconds) - 2
            if Power.idleSeconds() >= n && Date().timeIntervalSince(lastNudge) >= n {
                power.declareUserActivity()
                if settings.wiggleMouse && AXIsProcessTrusted() { Power.wiggleMouse() }
                lastNudge = Date()
            }
        }
        updateUI()
    }

    @objc func didWake() {
        if active { power.hold(keepScreenOn: settings.keepScreenOn) }
    }

    @objc func onSecondLaunch() {
        if !active { activate(nil) }
        showAlert("Kapeyn is already running",
                  "Your Mac is being kept awake. Look for the cup in the menu bar.")
    }

    func showAlert(_ title: String, _ text: String) {
        NSApp.activate(ignoringOtherApps: true)
        let alert = NSAlert()
        alert.messageText = title
        alert.informativeText = text
        alert.alertStyle = .informational
        alert.runModal()
    }

    // MARK: UI

    func statusText() -> String {
        if !active { return "Off - Mac may sleep" }
        guard let u = until else { return "Awake - indefinitely" }
        return "Awake - " + formatRemaining(u.timeIntervalSinceNow) + " left"
    }

    func formatRemaining(_ seconds: TimeInterval) -> String {
        let total = max(1, Int((seconds / 60).rounded(.up)))
        let h = total / 60, m = total % 60
        if h == 0 { return "\(total) min" }
        return m == 0 ? "\(h) h" : "\(h) h \(m) min"
    }

    func formatMinutes(_ minutes: Int) -> String {
        if minutes < 60 { return "\(minutes) minutes" }
        let h = minutes / 60
        return h == 1 ? "1 hour" : "\(h) hours"
    }

    func formatSeconds(_ seconds: Int) -> String {
        if seconds < 60 { return "\(seconds) seconds" }
        let m = seconds / 60
        return m == 1 ? "1 minute" : "\(m) minutes"
    }

    func updateUI() {
        statusItem.button?.image = active ? iconOn : iconOff
        statusItem.button?.toolTip = "Kapeyn: " + statusText()
    }

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

    func buildMenu() {
        menu.delegate = self
        menu.autoenablesItems = false

        statusLine = item("Kapeyn", nil)
        statusLine.isEnabled = false
        menu.addItem(statusLine)
        menu.addItem(.separator())

        toggleItem = item("Turn off", #selector(toggleClicked))
        menu.addItem(toggleItem)

        let durationItem = item("Keep awake for", nil)
        durationMenu.autoenablesItems = false
        durationMenu.addItem(item("Indefinitely", #selector(pickDuration(_:)), tag: 0))
        durationMenu.addItem(.separator())
        for m in durations {
            durationMenu.addItem(item(formatMinutes(m), #selector(pickDuration(_:)), tag: m))
        }
        durationItem.submenu = durationMenu
        menu.addItem(durationItem)
        menu.addItem(.separator())

        screenItem = item("Keep screen on", #selector(toggleScreen))
        menu.addItem(screenItem)

        activityItem = item("Prevent screen lock & Away status", #selector(toggleActivity))
        activityItem.toolTip = "When you're idle, Kapeyn tells macOS you're active so the screen saver and lock don't kick in."
        menu.addItem(activityItem)

        mouseItem = item("Also nudge the mouse (Teams/Slack)", #selector(toggleMouse))
        mouseItem.toolTip = "Moves the pointer 1 pixel and back while you're idle. Needs Accessibility permission."
        menu.addItem(mouseItem)

        intervalItem = item("Nudge when idle for", nil)
        intervalMenu.autoenablesItems = false
        for s in nudgeChoices {
            intervalMenu.addItem(item(formatSeconds(s), #selector(pickInterval(_:)), tag: s))
        }
        intervalItem.submenu = intervalMenu
        menu.addItem(intervalItem)
        menu.addItem(.separator())

        startActiveItem = item("Turn on when Kapeyn starts", #selector(toggleStartActive))
        menu.addItem(startActiveItem)
        loginItem = item("Start at login", #selector(toggleLogin))
        menu.addItem(loginItem)
        menu.addItem(.separator())

        menu.addItem(item("Quit Kapeyn", #selector(quit), key: "q"))
    }

    func menuNeedsUpdate(_ menu: NSMenu) {
        statusLine.title = "Kapeyn: " + statusText()
        toggleItem.title = active ? "Turn off" : "Turn on"
        for i in durationMenu.items where !i.isSeparatorItem {
            i.state = (i.tag == 0 && active && until == nil) ? .on : .off
        }
        screenItem.state = settings.keepScreenOn ? .on : .off
        activityItem.state = settings.simulateActivity ? .on : .off
        mouseItem.state = settings.wiggleMouse ? .on : .off
        mouseItem.title = settings.wiggleMouse && !AXIsProcessTrusted()
            ? "Also nudge the mouse (needs permission)"
            : "Also nudge the mouse (Teams/Slack)"
        mouseItem.isEnabled = settings.simulateActivity
        intervalItem.isEnabled = settings.simulateActivity
        for i in intervalMenu.items {
            i.state = i.tag == settings.nudgeSeconds ? .on : .off
        }
        startActiveItem.state = settings.startActive ? .on : .off
        loginItem.state = LoginItem.isEnabled ? .on : .off
    }

    // MARK: actions

    @objc func toggleClicked() { toggle() }

    @objc func pickDuration(_ sender: NSMenuItem) {
        activate(sender.tag == 0 ? nil : sender.tag)
    }

    @objc func toggleScreen() {
        settings.keepScreenOn.toggle()
        if active { power.hold(keepScreenOn: settings.keepScreenOn) }
    }

    @objc func toggleActivity() { settings.simulateActivity.toggle() }

    @objc func toggleMouse() {
        settings.wiggleMouse.toggle()
        if settings.wiggleMouse && !AXIsProcessTrusted() {
            // Shows the system prompt that opens Privacy & Security > Accessibility.
            _ = AXIsProcessTrustedWithOptions(["AXTrustedCheckOptionPrompt": true] as CFDictionary)
        }
    }

    @objc func pickInterval(_ sender: NSMenuItem) { settings.nudgeSeconds = sender.tag }

    @objc func toggleStartActive() { settings.startActive.toggle() }

    @objc func toggleLogin() {
        do {
            try LoginItem.set(!LoginItem.isEnabled)
        } catch {
            showAlert("Couldn't change the login setting", error.localizedDescription)
        }
    }

    @objc func quit() { NSApp.terminate(nil) }
}

// MARK: - Entry point

let args = CommandLine.arguments
if args.count == 3 && args[1] == "--export-iconset" {
    IconArt.exportIconset(to: args[2])
    exit(0)
}

let app = NSApplication.shared
let delegate = AppDelegate()
app.delegate = delegate
app.setActivationPolicy(.accessory) // menu bar only, no Dock icon
app.run()
