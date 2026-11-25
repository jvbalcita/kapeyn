import Foundation
import IOKit.pwr_mgt

// The "kapeyn" command. Talks to the menu bar app, starting it if needed.
enum CLI {
    static let verbs: Set<String> = ["on", "off", "toggle", "status", "run", "until", "then", "doctor", "guide",
                                     "help", "-h", "--help"]

    static let help = """
    kapeyn                      Show what Kapeyn is doing
    kapeyn on                   Keep awake until you turn it off
    kapeyn on 30m | 2h | 1h30m  Keep awake for a while
    kapeyn until downloads      Keep awake until downloads finish
    kapeyn until <app>          Keep awake until an app closes, e.g.  kapeyn until "Final Cut Pro"
    kapeyn run <command...>     Keep awake until the command finishes, e.g.  kapeyn run npm run build
    kapeyn then sleep | lock | shutdown | nothing
                                What to do when the time is up, or the download, app or command is done.
                                Or add it to the others:  kapeyn on 2h --then sleep
                                                          kapeyn run --then shutdown npm run build
    kapeyn off                  Turn off
    kapeyn toggle               Switch on/off
    kapeyn doctor               Check what locks or sleeps this Mac
    kapeyn guide                Open the full guide (also in the menu: Help)
    """

    static func fail(_ message: String) -> Int32 {
        FileHandle.standardError.write(Data((message + "\n").utf8))
        return 2
    }

    static func run(_ raw: [String]) -> Int32 {
        guard let verb = raw.first?.lowercased() else { return printReply(send("status")) }
        var args = raw
        switch verb {
        case "help", "-h", "--help":
            print(help)
            return 0
        case "on":
            guard let then = takeThen(&args, leadingOnly: false) else { return 2 }
            var reply: String?
            if args.count > 1 {
                let text = args.dropFirst().joined()
                guard let minutes = parseDuration(text) else {
                    return fail("kapeyn: couldn't understand \"\(text)\". Try 30m, 2h or 1h30m.")
                }
                reply = send("on \(minutes)")
            } else {
                reply = send("on")
            }
            if let word = then.word, reply != nil { reply = send("then " + word) }
            return printReply(reply)
        case "until":
            guard let then = takeThen(&args, leadingOnly: false) else { return 2 }
            var what = Array(args.dropFirst())
            if what.first?.lowercased() == "app" { what.removeFirst() }
            guard let first = what.first?.lowercased() else {
                return fail("usage: kapeyn until downloads | kapeyn until <app>   (add --then sleep, lock or shutdown)")
            }
            var reply = what.count == 1 && (first == "downloads" || first == "download")
                ? send("until downloads")
                : send("until app " + what.joined(separator: " "))
            if let word = then.word, let r = reply, !r.hasPrefix("error") { reply = send("then " + word) }
            return printReply(reply)
        case "then":
            guard let action = DoneAction(word: args.dropFirst().joined(separator: " ")) else {
                return fail("usage: kapeyn then sleep | lock | shutdown | nothing")
            }
            return printReply(send("then " + action.word))
        case "off", "toggle", "status", "guide":
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

    /// The result of looking for "--then X": `word` is the action word, or nil if there was no --then.
    struct ThenOption { let word: String? }

    /// Takes "--then X" or "--then=X" out of args (leadingOnly: only right at the start, for "run", so the
    /// command's own options are left alone). Returns nil, after printing a message, if X isn't an action.
    static func takeThen(_ args: inout [String], leadingOnly: Bool) -> ThenOption? {
        let first = leadingOnly ? 0 : 1 // "run" gets the args after "run"; the others still have the verb
        var i = first
        while i < args.count {
            let a = args[i]
            var value: String?
            var take = 0
            if a.lowercased().hasPrefix("--then=") {
                value = String(a.dropFirst(7))
                take = 1
            } else if a.lowercased() == "--then" {
                take = 2
                value = i + 1 < args.count ? args[i + 1] : ""
                // "--then shut down"
                if value?.lowercased() == "shut" && i + 2 < args.count && args[i + 2].lowercased() == "down" {
                    value = "shutdown"
                    take = 3
                }
            }
            if take == 0 {
                if leadingOnly { break }
                i += 1
                continue
            }
            guard let v = value, let action = DoneAction(word: v) else {
                let shown = value ?? ""
                FileHandle.standardError.write(Data(("kapeyn: --then needs sleep, lock, shutdown or nothing"
                    + (shown.isEmpty ? "" : " (not \"\(shown)\")") + ".\n").utf8))
                return nil
            }
            args.removeSubrange(i..<min(i + take, args.count))
            return ThenOption(word: action.word)
        }
        return ThenOption(word: nil)
    }

    static func runCommand(_ raw: [String]) -> Int32 {
        var cmd = raw
        guard let then = takeThen(&cmd, leadingOnly: true) else { return 2 }
        if cmd.first == "--" { cmd.removeFirst() }
        guard !cmd.isEmpty else {
            return fail("usage: kapeyn run [--then sleep|lock|shutdown] <command...>")
        }

        let me = getpid()
        let label = String(URL(fileURLWithPath: cmd[0]).lastPathComponent.prefix(40))
        let held: Bool
        if let word = then.word {
            // The app watches this process and does the action once it ends.
            held = send("until pid \(me) \(label)") != nil && send("then " + word) != nil
            if held {
                let doing = word == "sleep" ? "put the Mac to sleep" : word == "lock" ? "lock the Mac" : "shut down the Mac"
                FileHandle.standardError.write(Data(("kapeyn: " + (word == "nothing"
                    ? "keeping the Mac awake until \(label) finishes."
                    : "when \(label) finishes, Kapeyn will \(doing).") + "\n").utf8))
                // Stopped with Ctrl+C: end quietly instead of sleeping or shutting down.
                signal(SIGINT) { _ in
                    _ = IPCClient.send("then nothing", timeout: 1)
                    exit(130)
                }
            }
        } else {
            held = send("hold \(me) \(label)") != nil
        }
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

        if !held { IOPMAssertionRelease(ownAssertion) } else if then.word == nil { _ = IPCClient.send("release \(me)", timeout: 2) }
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
