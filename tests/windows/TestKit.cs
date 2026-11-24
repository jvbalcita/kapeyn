using System;
using System.Globalization;
using System.Threading;

// A tiny test runner, so the tests build with the C# compiler that ships with Windows (no NuGet, no SDK).
namespace Kapeyn.Tests
{
    static class T
    {
        public static int Passed, Failed;

        public static void Group(string name)
        {
            Console.WriteLine();
            Console.WriteLine(name);
        }

        public static void Check(string name, bool ok, string detail = null)
        {
            if (ok) Passed++; else Failed++;
            Console.ForegroundColor = ok ? ConsoleColor.Green : ConsoleColor.Red;
            Console.Write(ok ? "  PASS " : "  FAIL ");
            Console.ResetColor();
            Console.WriteLine(name + (detail != null ? "  (" + detail + ")" : ""));
        }

        public static void Eq<TValue>(string name, TValue expected, TValue actual)
        {
            bool ok = Equals(expected, actual);
            Check(name, ok, ok ? null : "expected <" + expected + "> but got <" + actual + ">");
        }

        /// Runs one group; an unexpected exception counts as a failure instead of stopping the run.
        public static void Run(string group, Action tests)
        {
            Group(group);
            try { tests(); }
            catch (Exception ex) { Check("no unexpected error", false, ex.GetType().Name + ": " + ex.Message); }
        }
    }

    static class TestMain
    {
        public static string RepoRoot;

        [STAThread]
        static int Main(string[] args)
        {
            RepoRoot = args.Length > 0 ? args[0] : ".";
            // Fixed culture so time formatting is the same on every machine.
            var en = new CultureInfo("en-US");
            Thread.CurrentThread.CurrentCulture = en;
            Thread.CurrentThread.CurrentUICulture = en;

            T.Run("Work hours schedule", ScheduleTests.Run);
            T.Run("Engine: on, off, timers", EngineTests.Basics);
            T.Run("Engine: automatic reasons and snooze", EngineTests.Snooze);
            T.Run("Engine: end of work hours", EngineTests.WorkHoursEnd);
            T.Run("Engine: low battery", EngineTests.Battery);
            T.Run("Formatting", FormatTests.Run);
            T.Run("Duration parsing (kapeyn on 1h30m)", DurationTests.Run);
            T.Run("Settings: save, load, migrate", SettingsTests.Run);
            T.Run("Download detection", NetMonitorTests.Run);
            T.Run("Lock doctor: auto nudge timing", DoctorTests.Run);
            T.Run("Help guide", HelpTests.Run);
            T.Run("Finding the app through links (winget)", PathTests.Run);

            Console.WriteLine();
            Console.ForegroundColor = T.Failed == 0 ? ConsoleColor.Green : ConsoleColor.Red;
            Console.WriteLine(T.Failed == 0
                ? "All " + T.Passed + " tests passed."
                : T.Failed + " of " + (T.Passed + T.Failed) + " tests FAILED.");
            Console.ResetColor();
            return T.Failed == 0 ? 0 : 1;
        }
    }
}
