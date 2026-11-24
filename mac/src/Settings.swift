import Foundation

let kBundleID = "com.kapeyn.Kapeyn"

struct WatchedApp: Equatable {
    var id: String    // bundle identifier
    var name: String  // display name
}

// UserDefaults: com.kapeyn.Kapeyn
final class Settings {
    private let d = UserDefaults.standard

    init() {
        d.register(defaults: [
            "StartActive": true,
            "KeepScreenOn": true,
            "SimulateActivity": true,
            "WiggleMouse": false,
            "NudgeSeconds": 0, // 0 = Auto
            "Welcomed": false,
            "WhileCall": true,
            "WhileDownloading": true,
            "ScheduleEnabled": false,
            "ScheduleDays": 0x3E, // bit per day, Sunday = bit 0 -> Mon..Fri
            "ScheduleStart": 9,
            "ScheduleEnd": 17,
            "ScheduleEndTurnsOff": true,
            "ScreenOffWhenLocked": true,
            "PauseBatteryPercent": 20,
            "Hotkey": true,
        ])
        // v1 offered 60 s as its default; move people who picked it to Auto once.
        if d.integer(forKey: "SettingsVersion") < 2 {
            if d.integer(forKey: "NudgeSeconds") == 60 { d.set(0, forKey: "NudgeSeconds") }
            d.set(2, forKey: "SettingsVersion")
        }
    }

    private func bool(_ k: String) -> Bool { return d.bool(forKey: k) }
    private func int(_ k: String) -> Int { return d.integer(forKey: k) }

    var startActive: Bool { get { return bool("StartActive") } set { d.set(newValue, forKey: "StartActive") } }
    var keepScreenOn: Bool { get { return bool("KeepScreenOn") } set { d.set(newValue, forKey: "KeepScreenOn") } }
    var simulateActivity: Bool { get { return bool("SimulateActivity") } set { d.set(newValue, forKey: "SimulateActivity") } }
    var wiggleMouse: Bool { get { return bool("WiggleMouse") } set { d.set(newValue, forKey: "WiggleMouse") } }
    var welcomed: Bool { get { return bool("Welcomed") } set { d.set(newValue, forKey: "Welcomed") } }
    var whileCall: Bool { get { return bool("WhileCall") } set { d.set(newValue, forKey: "WhileCall") } }
    var whileDownloading: Bool { get { return bool("WhileDownloading") } set { d.set(newValue, forKey: "WhileDownloading") } }
    var scheduleEnabled: Bool { get { return bool("ScheduleEnabled") } set { d.set(newValue, forKey: "ScheduleEnabled") } }
    var scheduleEndTurnsOff: Bool { get { return bool("ScheduleEndTurnsOff") } set { d.set(newValue, forKey: "ScheduleEndTurnsOff") } }
    var screenOffWhenLocked: Bool { get { return bool("ScreenOffWhenLocked") } set { d.set(newValue, forKey: "ScreenOffWhenLocked") } }
    var hotkey: Bool { get { return bool("Hotkey") } set { d.set(newValue, forKey: "Hotkey") } }

    var nudgeSeconds: Int {
        get { let n = int("NudgeSeconds"); return n == 0 || (10...3600).contains(n) ? n : 0 }
        set { d.set(newValue, forKey: "NudgeSeconds") }
    }
    var scheduleDays: Int { get { return int("ScheduleDays") & 0x7F } set { d.set(newValue & 0x7F, forKey: "ScheduleDays") } }
    var scheduleStart: Int { get { return min(23, max(0, int("ScheduleStart"))) } set { d.set(newValue, forKey: "ScheduleStart") } }
    var scheduleEnd: Int {
        get { let e = min(24, max(1, int("ScheduleEnd"))); return e > scheduleStart ? e : min(24, scheduleStart + 8) }
        set { d.set(newValue, forKey: "ScheduleEnd") }
    }
    var pauseBatteryPercent: Int {
        get { return min(90, max(0, int("PauseBatteryPercent"))) }
        set { d.set(newValue, forKey: "PauseBatteryPercent") }
    }

    var watchApps: [WatchedApp] {
        get {
            let raw = d.array(forKey: "WatchApps") as? [[String: String]] ?? []
            return raw.compactMap { e in
                guard let id = e["id"], let name = e["name"] else { return nil }
                return WatchedApp(id: id, name: name)
            }
        }
        set { d.set(newValue.map { ["id": $0.id, "name": $0.name] }, forKey: "WatchApps") }
    }
}

// Start at login (~/Library/LaunchAgents, no admin needed)
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
