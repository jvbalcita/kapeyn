import AppKit
import CoreAudio
import IOKit.ps

// Things that can keep the Mac awake automatically.
enum Triggers {
    // MARK: work hours

    /// True inside the work-hours window (ignores whether the schedule is switched on).
    /// A window that ends earlier than it starts runs overnight and belongs to the day it started.
    static func inWorkHours(_ s: Settings, _ date: Date = Date()) -> Bool {
        let cal = Calendar.current
        let t = cal.component(.hour, from: date) * 60 + cal.component(.minute, from: date)
        let today = cal.component(.weekday, from: date) - 1 // 0 = Sunday
        let yesterday = (today + 6) % 7
        let days = s.scheduleDays
        let start = s.scheduleStartMin, end = s.scheduleEndMin
        if start < end { return days & (1 << today) != 0 && t >= start && t < end }
        return (t >= start && days & (1 << today) != 0) || (t < end && days & (1 << yesterday) != 0)
    }

    // MARK: on a call (some app is using a microphone)

    static func micInUse() -> Bool {
        let system = AudioObjectID(kAudioObjectSystemObject)
        var devicesAddr = AudioObjectPropertyAddress(mSelector: kAudioHardwarePropertyDevices,
                                                     mScope: kAudioObjectPropertyScopeGlobal,
                                                     mElement: kAudioObjectPropertyElementMain)
        var size: UInt32 = 0
        guard AudioObjectGetPropertyDataSize(system, &devicesAddr, 0, nil, &size) == noErr, size > 0 else { return false }
        var ids = [AudioDeviceID](repeating: 0, count: Int(size) / MemoryLayout<AudioDeviceID>.size)
        guard AudioObjectGetPropertyData(system, &devicesAddr, 0, nil, &size, &ids) == noErr else { return false }

        for id in ids {
            // Only devices that can record.
            var streamsAddr = AudioObjectPropertyAddress(mSelector: kAudioDevicePropertyStreams,
                                                         mScope: kAudioDevicePropertyScopeInput,
                                                         mElement: kAudioObjectPropertyElementMain)
            var streamsSize: UInt32 = 0
            guard AudioObjectGetPropertyDataSize(id, &streamsAddr, 0, nil, &streamsSize) == noErr, streamsSize > 0 else { continue }

            var runningAddr = AudioObjectPropertyAddress(mSelector: kAudioDevicePropertyDeviceIsRunningSomewhere,
                                                         mScope: kAudioObjectPropertyScopeGlobal,
                                                         mElement: kAudioObjectPropertyElementMain)
            var running: UInt32 = 0
            var runningSize = UInt32(MemoryLayout<UInt32>.size)
            if AudioObjectGetPropertyData(id, &runningAddr, 0, nil, &runningSize, &running) == noErr && running != 0 {
                return true
            }
        }
        return false
    }

    // MARK: watched apps

    static func isRunning(_ bundleID: String) -> Bool {
        return !NSRunningApplication.runningApplications(withBundleIdentifier: bundleID).isEmpty
    }

    private static let boring: Set<String> = [kBundleID, "com.apple.finder"]

    /// Regular (Dock) apps that are open now, for the "Add an open app" menu.
    static func runningApps() -> [WatchedApp] {
        var seen = Set<String>()
        var out: [WatchedApp] = []
        for app in NSWorkspace.shared.runningApplications where app.activationPolicy == .regular {
            guard let id = app.bundleIdentifier, !boring.contains(id), !seen.contains(id) else { continue }
            seen.insert(id)
            out.append(WatchedApp(id: id, name: app.localizedName ?? id))
        }
        return out.sorted { $0.name.localizedCaseInsensitiveCompare($1.name) == .orderedAscending }
    }

    // MARK: command-line holds

    static func isAlive(_ pid: Int32) -> Bool {
        return kill(pid, 0) == 0 || errno == EPERM
    }

    // MARK: battery

    /// (onBattery, percent 0...100 or -1 if unknown)
    static func battery() -> (onBattery: Bool, percent: Int) {
        guard let info = IOPSCopyPowerSourcesInfo()?.takeRetainedValue(),
              let list = IOPSCopyPowerSourcesList(info)?.takeRetainedValue() as? [CFTypeRef]
        else { return (false, -1) }
        for source in list {
            guard let desc = IOPSGetPowerSourceDescription(info, source)?.takeUnretainedValue() as? [String: Any]
            else { continue }
            let state = desc["Power Source State"] as? String
            let current = desc["Current Capacity"] as? Int ?? -1
            let maximum = desc["Max Capacity"] as? Int ?? 100
            let percent = current >= 0 && maximum > 0 ? current * 100 / maximum : -1
            return (state == "Battery Power", percent)
        }
        return (false, -1)
    }
}

/// "Downloading or streaming": bytes received across network interfaces, sampled about every 5 s.
/// Busy when 3 of the last 4 samples are above ~150 KB/s (one-off sync bursts don't count),
/// and stays busy for a minute after it drops.
final class NetMonitor {
    static let threshold = 150.0 * 1024
    private var lastPerInterface: [String: UInt32] = [:]
    private var lastTime = Date.distantPast
    private var recent: [Bool] = []
    private var busyUntil = Date.distantPast
    private(set) var bytesPerSecond: Double = 0

    func update() -> Bool {
        let now = Date()
        if !lastPerInterface.isEmpty && now.timeIntervalSince(lastTime) < 4 { return now < busyUntil }

        var ifap: UnsafeMutablePointer<ifaddrs>?
        guard getifaddrs(&ifap) == 0, let first = ifap else { return now < busyUntil }
        defer { freeifaddrs(ifap) }

        var counters: [String: UInt32] = [:]
        var cursor: UnsafeMutablePointer<ifaddrs>? = first
        while let entry = cursor {
            let ifa = entry.pointee
            if let addr = ifa.ifa_addr, addr.pointee.sa_family == UInt8(AF_LINK), let data = ifa.ifa_data {
                let name = String(cString: ifa.ifa_name)
                if !name.hasPrefix("lo") {
                    counters[name] = data.assumingMemoryBound(to: if_data.self).pointee.ifi_ibytes
                }
            }
            cursor = ifa.ifa_next
        }
        return sample(counters, now: now)
    }

    /// Feeds one reading of each interface's received-bytes counter. Returns whether downloading counts as active.
    func sample(_ counters: [String: UInt32], now: Date) -> Bool {
        let elapsed = now.timeIntervalSince(lastTime)
        if !lastPerInterface.isEmpty && elapsed < 4 { return now < busyUntil } // called early: keep the answer

        var delta: UInt64 = 0
        for (name, bytes) in counters {
            if let previous = lastPerInterface[name] { delta += UInt64(bytes &- previous) } // 32-bit counters wrap
        }

        let hadBaseline = !lastPerInterface.isEmpty
        lastPerInterface = counters
        lastTime = now
        guard hadBaseline, elapsed > 0 else { return false }

        bytesPerSecond = Double(delta) / elapsed
        recent.append(bytesPerSecond >= NetMonitor.threshold)
        if recent.count > 4 { recent.removeFirst(recent.count - 4) }
        if recent.filter({ $0 }).count >= 3 { busyUntil = now.addingTimeInterval(60) }
        return now < busyUntil
    }
}
