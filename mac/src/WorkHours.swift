import AppKit

/// All work-hours settings in one dialog: on/off, days, any start/end time (overnight allowed),
/// and what happens when work hours end.
final class WorkHoursPanel: NSObject {
    private let settings: Settings
    private let enabled = NSButton(checkboxWithTitle: "Keep my Mac awake during work hours", target: nil, action: nil)
    private var dayBoxes: [Int: NSButton] = [:]
    private let from = NSDatePicker()
    private let to = NSDatePicker()
    private let note = NSTextField(labelWithString: "")
    private let endOff = NSButton(checkboxWithTitle: "Turn Kapeyn off", target: nil, action: nil)
    private let endLock = NSButton(checkboxWithTitle: "Also lock the Mac", target: nil, action: nil)
    private let endShutdown = NSButton(checkboxWithTitle: "Also shut down the Mac (you get 60 seconds to cancel)", target: nil, action: nil)
    private var saveButton: NSButton?

    init(settings: Settings) {
        self.settings = settings
        super.init()
    }

    /// Shows the dialog. Returns true if the user saved.
    func run() -> Bool {
        enabled.state = settings.scheduleEnabled ? .on : .off
        enabled.font = NSFont.boldSystemFont(ofSize: NSFont.systemFontSize)
        enabled.target = self
        enabled.action = #selector(changed)

        let symbols = Calendar.current.shortWeekdaySymbols // Sunday first
        var dayViews: [NSView] = []
        for i in 1...7 {
            let d = i % 7 // Monday first
            let box = NSButton(checkboxWithTitle: symbols[d], target: self, action: #selector(changed))
            box.state = settings.scheduleDays & (1 << d) != 0 ? .on : .off
            dayBoxes[d] = box
            dayViews.append(box)
        }

        for picker in [from, to] {
            picker.datePickerStyle = .textFieldAndStepper
            picker.datePickerElements = .hourMinute
            picker.target = self
            picker.action = #selector(changed)
        }
        from.dateValue = date(settings.scheduleStartMin)
        to.dateValue = date(settings.scheduleEndMin)
        note.textColor = .secondaryLabelColor

        endOff.state = settings.scheduleEndTurnsOff ? .on : .off
        endLock.state = settings.endLock ? .on : .off
        endShutdown.state = settings.endShutdown ? .on : .off
        endOff.target = self
        endOff.action = #selector(changed)
        endLock.target = self
        endLock.action = #selector(lockChanged)
        endShutdown.target = self
        endShutdown.action = #selector(shutdownChanged)

        let stack = NSStackView(views: [
            enabled,
            heading("Days"), NSStackView(views: dayViews),
            heading("Hours"), NSStackView(views: [label("From"), from, label("to"), to]),
            note,
            heading("When work hours end"), endOff, indent(endLock), indent(endShutdown),
        ])
        stack.orientation = .vertical
        stack.alignment = .leading
        stack.spacing = 8
        update()
        let fit = stack.fittingSize
        stack.setFrameSize(NSSize(width: max(480, fit.width), height: fit.height))

        let alert = NSAlert()
        alert.messageText = "Work hours"
        alert.accessoryView = stack
        saveButton = alert.addButton(withTitle: "Save")
        alert.addButton(withTitle: "Cancel")
        update()

        NSApp.activate(ignoringOtherApps: true)
        guard alert.runModal() == .alertFirstButtonReturn else { return false }
        apply()
        return true
    }

    private func heading(_ text: String) -> NSTextField {
        let l = NSTextField(labelWithString: text)
        l.font = NSFont.boldSystemFont(ofSize: NSFont.systemFontSize)
        return l
    }

    private func label(_ text: String) -> NSTextField { return NSTextField(labelWithString: text) }

    private func indent(_ view: NSView) -> NSView {
        let row = NSStackView(views: [view])
        row.edgeInsets = NSEdgeInsets(top: 0, left: 20, bottom: 0, right: 0)
        return row
    }

    private func date(_ minutes: Int) -> Date {
        return Calendar.current.date(bySettingHour: minutes / 60, minute: minutes % 60, second: 0, of: Date()) ?? Date()
    }

    private func minutes(_ picker: NSDatePicker) -> Int {
        let c = Calendar.current.dateComponents([.hour, .minute], from: picker.dateValue)
        return (c.hour ?? 0) * 60 + (c.minute ?? 0)
    }

    private var dayMask: Int {
        var mask = 0
        for (d, box) in dayBoxes where box.state == .on { mask |= 1 << d }
        return mask
    }

    @objc private func changed() { update() }

    @objc private func lockChanged() {
        if endLock.state == .on { endShutdown.state = .off }
        update()
    }

    @objc private func shutdownChanged() {
        if endShutdown.state == .on { endLock.state = .off }
        update()
    }

    private func update() {
        let on = enabled.state == .on
        for box in dayBoxes.values { box.isEnabled = on }
        from.isEnabled = on
        to.isEnabled = on
        endOff.isEnabled = on
        endLock.isEnabled = on && endOff.state == .on
        endShutdown.isEnabled = on && endOff.state == .on

        let a = minutes(from), b = minutes(to)
        var ok = true
        if a == b {
            note.stringValue = "Start and end can't be the same time."
            ok = false
        } else if dayMask == 0 {
            note.stringValue = "Pick at least one day."
            ok = false
        } else if b < a {
            note.stringValue = "Overnight: ends at \(Format.timeOfDay(b)) the next day (\(Format.duration((1440 - a + b) * 60)))."
        } else {
            note.stringValue = "\(Format.duration((b - a) * 60)) a day."
        }
        saveButton?.isEnabled = ok || !on
    }

    private func apply() {
        settings.scheduleEnabled = enabled.state == .on
        let a = minutes(from), b = minutes(to)
        if a != b && dayMask != 0 {
            settings.scheduleDays = dayMask
            settings.scheduleStartMin = a
            settings.scheduleEndMin = b
        }
        let off = endOff.state == .on
        settings.scheduleEndTurnsOff = off
        settings.endLock = off && endLock.state == .on
        settings.endShutdown = off && endShutdown.state == .on
    }
}

enum SystemActions {
    /// Locks the screen right away (same as Control-Command-Q).
    static func lockScreen() {
        typealias LockFunction = @convention(c) () -> Int32
        if let handle = dlopen("/System/Library/PrivateFrameworks/login.framework/Versions/Current/login", RTLD_LAZY),
           let symbol = dlsym(handle, "SACLockScreenImmediate") {
            _ = unsafeBitCast(symbol, to: LockFunction.self)()
            return
        }
        // Fallback: sleep the display, which locks when a password is required after sleep.
        let p = Process()
        p.executableURL = URL(fileURLWithPath: "/usr/bin/pmset")
        p.arguments = ["displaysleepnow"]
        try? p.run()
    }

    /// Asks macOS for a normal shutdown (apps can still ask you to save). Returns an error message on failure.
    /// The first time, macOS asks whether Kapeyn may control System Events.
    static func shutDown() -> String? {
        var error: NSDictionary?
        NSAppleScript(source: "tell application \"System Events\" to shut down")?.executeAndReturnError(&error)
        guard let e = error else { return nil }
        return e[NSAppleScript.errorMessage] as? String ?? "unknown error"
    }
}
