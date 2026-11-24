import Foundation
import IOKit.pwr_mgt

// The "kapeyn" command. Talks to the menu bar app, starting it if needed.
enum CLI {
    static let verbs: Set<String> = ["on", "off", "toggle", "status", "run", "doctor", "help", "-h", "--help"]

    static let help = """
    kapeyn                      Show what Kapeyn is doing
    kapeyn on                   Keep awake until you turn it off
    kapeyn on 30m | 2h | 1h30m  Keep awake for a while
    kapeyn off                  Turn off
    kapeyn toggle               Switch on/off
    kapeyn run <command...>     Keep awake until the command finishes, e.g.  kapeyn run npm run build
    kapeyn doctor               Check what locks or sleeps this Mac
    """

    static func run(_ args: [String]) -> Int32 {
        guard let verb = args.first?.lowercased() else { return printReply(send("status")) }
        switch verb {
        case "help", "-h", "--help":
            print(help)
            return 0
        case "on":
            if args.count > 1 {
                let text = args.dropFirst().joined()
                guard let minutes = parseDuration(text) else {
                    FileHandle.standardError.write(Data("kapeyn: couldn't understand \"\(text)\". Try 30m, 2h or 1h30m.\n".utf8))
                    return 2
                }
                return printReply(send("on \(minutes)"))
            }
            return printReply(send("on"))
        case "off", "toggle", "status":
            return printReply(send(verb))
        case "doctor":
            return printReply(send(verb, timeout: 30))
        case "run":
            return runCommand(Array(args.dropFirst()))
        default:
            FileHandle.standardError.write(Data("kapeyn: unknown command \"\(args[0])\"\n\n\(help)\n".utf8))
            return 2
        }
    }

    static func printReply(_ reply: String?) -> Int32 {
        guard let r = reply else {
            FileHandle.standardError.write(Data(("kapeyn: couldn't reach the Kapeyn app. If an older version is running, "
                + "quit it from the menu bar and try again.\n").utf8))
            return 1
        }
        print(r.trimmingCharacters(in: .whitespacesAndNewlines))
        return r.hasPrefix("error") ? 1 : 0
    }

    /// Sends a command to the app, starting it first if it isn't running.
    static func send(_ command: String, timeout: Double = 5) -> String? {
        if let r = IPCClient.send(command, timeout: timeout) { return r }
        guard launchApp() else { return nil }
        for _ in 0..<40 {
            usleep(250_000)
            if let r = IPCClient.send(command, timeout: timeout) { return r }
        }
        return nil
    }

    /// .../Kapeyn.app/Contents/MacOS/Kapeyn  ->  .../Kapeyn.app (follows a symlink in PATH)
    static func appBundleURL() -> URL? {
        let exe = URL(fileURLWithPath: Bundle.main.executablePath ?? CommandLine.arguments[0]).resolvingSymlinksInPath()
        let bundle = exe.deletingLastPathComponent().deletingLastPathComponent().deletingLastPathComponent()
        return bundle.pathExtension == "app" ? bundle : nil
    }

    static func launchApp() -> Bool {
        guard let bundle = appBundleURL() else { return false }
        let p = Process()
        p.executableURL = URL(fileURLWithPath: "/usr/bin/open")
        p.arguments = ["-g", "-a", bundle.path, "--args", "--cli-launch"]
        do {
            try p.run()
            p.waitUntilExit()
            return p.terminationStatus == 0
        } catch {
            return false
        }
    }

    static func runCommand(_ raw: [String]) -> Int32 {
        var cmd = raw
        if cmd.first == "--" { cmd.removeFirst() }
        guard !cmd.isEmpty else {
            FileHandle.standardError.write(Data("usage: kapeyn run <command...>\n".utf8))
            return 2
        }

        let me = getpid()
        let label = String(URL(fileURLWithPath: cmd[0]).lastPathComponent.prefix(40))
        let held = send("hold \(me) \(label)") != nil
        var ownAssertion: IOPMAssertionID = 0
        if !held {
            FileHandle.standardError.write(Data("kapeyn: couldn't reach the Kapeyn app; keeping the Mac awake from here instead.\n".utf8))
            IOPMAssertionCreateWithName("PreventUserIdleSystemSleep" as CFString,
                                        IOPMAssertionLevel(kIOPMAssertionLevelOn),
                                        "kapeyn run \(label)" as CFString, &ownAssertion)
        }

        let p = Process()
        p.executableURL = URL(fileURLWithPath: "/usr/bin/env")
        p.arguments = cmd
        do {
            try p.run()
        } catch {
            FileHandle.standardError.write(Data("kapeyn: couldn't start \(cmd[0]): \(error.localizedDescription)\n".utf8))
            return 127
        }
        p.waitUntilExit()

        if held { _ = IPCClient.send("release \(me)", timeout: 2) } else { IOPMAssertionRelease(ownAssertion) }
        return p.terminationReason == .uncaughtSignal ? 128 + p.terminationStatus : p.terminationStatus
    }

    /// "90", "30m", "2h", "1h30m", "1 hour 30 minutes" -> minutes
    static func parseDuration(_ text: String) -> Int? {
        let s = text.lowercased().replacingOccurrences(of: " ", with: "")
        if let n = Int(s) { return n > 0 ? n : nil }
        var total = 0
        var number = ""
        var unit = ""
        var sawUnit = false
        func flush() -> Bool {
            guard let n = Int(number) else { return false }
            if unit.hasPrefix("h") { total += n * 60 } else if unit.hasPrefix("m") { total += n } else { return false }
            number = ""
            unit = ""
            sawUnit = true
            return true
        }
        for c in s {
            if c.isNumber {
                if !unit.isEmpty { guard flush() else { return nil } }
                number.append(c)
            } else if c.isLetter {
                guard !number.isEmpty else { return nil }
                unit.append(c)
            } else {
                return nil
            }
        }
        if !number.isEmpty { guard !unit.isEmpty, flush() else { return nil } }
        return sawUnit && total > 0 ? total : nil
    }
}
