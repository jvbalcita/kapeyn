import Foundation

let kBundleID = "com.kapeyn.Kapeyn"

struct WatchedApp: Equatable {
    var id: String    // bundle identifier
    var name: String  // display name
}

// UserDefaults: com.kapeyn.Kapeyn
final class Settings {
    private let d: UserDefaults

    /// Tests pass their own (throwaway) defaults; the app uses the standard ones.
    init(defaults: UserDefaults = .standard) {
        d = defaults
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
            "ScheduleStartMin": 9 * 60, // minutes after midnight
            "ScheduleEndMin": 17 * 60,  // earlier than start = ends the next day
            "ScheduleEndTurnsOff": true,
            "EndLock": false,
            "EndShutdown": false,
            "ScreenOffWhenLocked": true,
            "PauseBatteryPercent": 20,
            "Hotkey": true,
        ])
        // v1 offered 60 s as its default; move people who picked it to Auto once.
        if d.integer(forKey: "SettingsVersion") < 2 {
            if d.integer(forKey: "NudgeSeconds") == 60 { d.set(0, forKey: "NudgeSeconds") }
            d.set(2, forKey: "SettingsVersion")
        }
        // v2 stored work hours as whole hours; v3 uses minutes.
        if d.integer(forKey: "SettingsVersion") < 3 {
            if let h = d.object(forKey: "ScheduleStart") as? Int { d.set(h * 60, forKey: "ScheduleStartMin") }
            if let h = d.object(forKey: "ScheduleEnd") as? Int { d.set((h * 60) % 1440, forKey: "ScheduleEndMin") }
            d.set(3, forKey: "SettingsVersion")
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
    var endLock: Bool { get { return bool("EndLock") } set { d.set(newValue, forKey: "EndLock") } }
    var endShutdown: Bool { get { return bool("EndShutdown") } set { d.set(newValue, forKey: "EndShutdown") } }

    var scheduleStartMin: Int {
        get { return min(1439, max(0, int("ScheduleStartMin"))) }
        set { d.set(newValue, forKey: "ScheduleStartMin") }
    }
    var scheduleEndMin: Int {
        get {
            let e = min(1439, max(0, int("ScheduleEndMin")))
            return e == scheduleStartMin ? (e + 8 * 60) % 1440 : e
        }
        set { d.set(newValue, forKey: "ScheduleEndMin") }
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

    /// A path that survives upgrades: Homebrew's versioned ".../Cellar/kapeyn/2.3.0/Kapeyn.app"
    /// becomes its stable ".../opt/kapeyn/Kapeyn.app". Other paths are unchanged.
    static func stablePath(_ path: String) -> String {
        guard let r = path.range(of: "/Cellar/kapeyn/[^/]+/", options: .regularExpression) else { return path }
        return path.replacingCharacters(in: r, with: "/opt/kapeyn/")
    }

    static var appPath: String { return stablePath(Bundle.main.bundlePath) }

    static func set(_ enabled: Bool) throws {
        if enabled {
            let dict: [String: Any] = [
                "Label": label,
                "ProgramArguments": ["/usr/bin/open", "-a", appPath],
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
        if args.last != appPath { try? set(true) }
    }
}
