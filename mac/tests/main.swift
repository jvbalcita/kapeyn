// Kapeyn's macOS tests: a tiny runner compiled together with the app's sources (minus src/main.swift).
// Needs only the Swift compiler from the Command Line Tools. Run with: bash mac/tests/run.sh
// Kept in step with tests/windows on Windows.
import Foundation

var passed = 0
var failed = 0

func group(_ name: String) { print("\n" + name) }

func check(_ name: String, _ ok: Bool, _ detail: String? = nil) {
    if ok { passed += 1 } else { failed += 1 }
    print((ok ? "  PASS " : "  FAIL ") + name + (detail.map { "  (\($0))" } ?? ""))
}

func eq<T: Equatable>(_ name: String, _ expected: T, _ actual: T) {
    check(name, expected == actual, expected == actual ? nil : "expected <\(expected)> but got <\(actual)>")
}

let repoRoot = CommandLine.arguments.count > 1 ? CommandLine.arguments[1] : ".."

group("Work hours schedule"); scheduleTests()
group("Engine: on, off, timers"); engineBasics()
group("Engine: automatic reasons and snooze"); engineSnooze()
group("Engine: end of work hours"); engineWorkHoursEnd()
group("Engine: low battery"); engineBattery()
group("Engine: when it's done (sleep, lock, shut down)"); engineWhenDone()
group("Command line: --then"); thenOptionTests()
group("Formatting"); formatTests()
group("Duration parsing (kapeyn on 1h30m)"); durationTests()
group("Settings: defaults and migration"); settingsTests()
group("Download detection"); netMonitorTests()
group("Lock doctor: auto nudge timing"); doctorTests()
group("Help guide"); helpTests()

print(failed == 0 ? "\nAll \(passed) tests passed." : "\n\(failed) of \(passed + failed) tests FAILED.")
exit(failed == 0 ? 0 : 1)
