// Kapeyn for macOS - keeps your Mac awake. Build/install with: bash install.sh
//
// The same binary is both the menu bar app and the "kapeyn" command:
//   kapeyn on 2h | off | status | run <command...> | doctor
// When run with one of those words (or from a terminal with no arguments) it acts as the command
// and talks to the running app over a local message port, starting the app if needed.

import AppKit

let launchArgs = Array(CommandLine.arguments.dropFirst())

if launchArgs.count == 2 && launchArgs[0] == "--export-iconset" {
    IconArt.exportIconset(to: launchArgs[1])
    exit(0)
}
if let first = launchArgs.first, CLI.verbs.contains(first.lowercased()) {
    exit(CLI.run(launchArgs))
}
if launchArgs.isEmpty && isatty(STDIN_FILENO) != 0 && isatty(STDOUT_FILENO) != 0 {
    exit(CLI.run([])) // typed "kapeyn" in a terminal: show status
}

let app = NSApplication.shared
let delegate = AppDelegate()
delegate.cliLaunch = launchArgs.contains("--cli-launch")
app.delegate = delegate
app.setActivationPolicy(.accessory) // menu bar only, no Dock icon
app.run()
