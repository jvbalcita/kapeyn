using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace Kapeyn.Tests
{
    static class ScheduleTests
    {
        static readonly DateTime Monday = new DateTime(2026, 11, 23); // a Monday

        static Settings Hours(int start, int end, int days = 0x3E)
        {
            var s = new Settings();
            s.ScheduleDays = days;
            s.ScheduleStartMin = start;
            s.ScheduleEndMin = end;
            return s;
        }

        static bool In(Settings s, int dayOffset, int hour, int minute = 0)
        {
            return Triggers.InWorkHours(s, Monday.AddDays(dayOffset).AddHours(hour).AddMinutes(minute));
        }

        public static void Run()
        {
            var day = Hours(13 * 60, 22 * 60); // Mon-Fri 1 PM - 10 PM
            T.Check("Mon 12:59 is before work", !In(day, 0, 12, 59));
            T.Check("Mon 13:00 is the first working minute", In(day, 0, 13));
            T.Check("Mon 21:59 is the last working minute", In(day, 0, 21, 59));
            T.Check("Mon 22:00 is after work", !In(day, 0, 22));
            T.Check("Saturday is off", !In(day, 5, 15));
            T.Check("Sunday is off", !In(day, 6, 15));

            var night = Hours(22 * 60, 6 * 60); // overnight 10 PM - 6 AM, Mon-Fri
            T.Check("overnight: Mon 23:00 inside", In(night, 0, 23));
            T.Check("overnight: Tue 02:00 inside (Monday's shift)", In(night, 1, 2));
            T.Check("overnight: Mon 02:00 outside (Sunday's shift is off)", !In(night, 0, 2));
            T.Check("overnight: Sat 02:00 inside (Friday's shift)", In(night, 5, 2));
            T.Check("overnight: Sun 02:00 outside (Saturday's shift is off)", !In(night, 6, 2));
            T.Check("overnight: Tue 06:00 is the end", !In(night, 1, 6));
            T.Check("overnight: Sat 23:00 outside (Saturday off)", !In(night, 5, 23));

            var toMidnight = Hours(18 * 60, 0); // 6 PM - midnight
            T.Check("to midnight: Mon 23:59 inside", In(toMidnight, 0, 23, 59));
            T.Check("to midnight: Tue 00:00 outside", !In(toMidnight, 1, 0));

            var minutes = Hours(9 * 60 + 30, 17 * 60 + 45); // 9:30 - 17:45
            T.Check("minutes: 9:29 outside", !In(minutes, 0, 9, 29));
            T.Check("minutes: 9:30 inside", In(minutes, 0, 9, 30));
            T.Check("minutes: 17:44 inside", In(minutes, 0, 17, 44));
            T.Check("minutes: 17:45 outside", !In(minutes, 0, 17, 45));

            T.Check("no days ticked: never inside", !In(Hours(9 * 60, 17 * 60, 0), 0, 12));
            T.Check("weekend only: Saturday inside", In(Hours(9 * 60, 17 * 60, 0x41), 5, 12));
        }
    }

    static class EngineTests
    {
        static readonly DateTime T0 = new DateTime(2026, 11, 23, 10, 0, 0);
        static readonly List<Reason> None = new List<Reason>();

        static Settings S()
        {
            var s = new Settings();
            s.PauseBatteryPercent = 20;
            return s;
        }

        static List<Reason> R(params string[] keyLabel)
        {
            var list = new List<Reason>();
            for (int i = 0; i + 1 < keyLabel.Length; i += 2) list.Add(new Reason(keyLabel[i], keyLabel[i + 1]));
            return list;
        }

        static StepEvents Step(Engine e, Settings s, DateTime t, List<Reason> auto = null, bool inWindow = false,
                               bool onBattery = false, int pct = -1)
        {
            return e.Step(s, t, inWindow, auto ?? None, onBattery, pct);
        }

        public static void Basics()
        {
            var s = S();
            var e = new Engine("PC", false);
            Step(e, s, T0);
            T.Check("starts off", !e.Awake);
            T.Eq("status when off", "Off - PC may sleep", e.StatusLine());

            e.TurnOn(null, T0);
            Step(e, s, T0);
            T.Check("turned on: awake", e.Awake);
            T.Eq("status when on by hand", "Awake", e.StatusLine());
            T.Check("on until turned off has no end time", !e.ManualUntil.HasValue && !e.ManualMinutes.HasValue);

            e.TurnOff();
            Step(e, s, T0);
            T.Check("turned off: not awake", !e.Awake);

            e.TurnOn(60, T0);
            Step(e, s, T0);
            T.Eq("timed: status shows time left", "Awake - 1 h left", e.StatusLine());
            T.Eq("timed: remembers the choice", (int?)60, e.ManualMinutes);
            Step(e, s, T0.AddMinutes(18));
            T.Eq("timed: counts down", "Awake - 42 min left", e.StatusLine());
            var ev = Step(e, s, T0.AddMinutes(59).AddSeconds(30));
            T.Check("timed: still awake just before the end", e.Awake && !ev.TimerEnded);
            ev = Step(e, s, T0.AddMinutes(60));
            T.Check("timed: switches off at the end", !e.Awake && ev.TimerEnded);
            ev = Step(e, s, T0.AddMinutes(61));
            T.Check("timed: 'timer ended' is reported once", !ev.TimerEnded);

            e.TurnOn(30, T0);
            e.TurnOn(240, T0);
            Step(e, s, T0);
            T.Eq("picking another time restarts the countdown", "Awake - 4 h left", e.StatusLine());
        }

        public static void Snooze()
        {
            var s = S();
            var e = new Engine("PC", false);
            var call = R("call", "on a call (Teams)");

            Step(e, s, T0, call);
            T.Check("a call keeps it awake", e.Awake);
            T.Eq("status says why", "Awake - on a call (Teams)", e.StatusLine());

            e.TurnOff();
            Step(e, s, T0.AddSeconds(5), call);
            T.Check("turning off during a call stays off (snoozed)", !e.Awake);
            T.Check("the call is snoozed", e.IsSnoozed("call"));

            Step(e, s, T0.AddSeconds(10));
            T.Check("call ended: snooze forgotten", !e.IsSnoozed("call"));
            Step(e, s, T0.AddSeconds(15), call);
            T.Check("the next call wakes it again", e.Awake);

            e.TurnOff();
            Step(e, s, T0.AddSeconds(20), call);
            e.TurnOn(null, T0.AddSeconds(25));
            Step(e, s, T0.AddSeconds(25), call);
            T.Check("turning on clears snoozes", e.Awake && !e.IsSnoozed("call"));
            T.Eq("manual plus call in the status", "Awake - turned on, on a call (Teams)", e.StatusLine());

            var e2 = new Engine("PC", false);
            var run = R("cli:4242", "running npm");
            Step(e2, s, T0, run);
            T.Eq("kapeyn run shows in the status", "Awake - running npm", e2.StatusLine());
            e2.TurnOff();
            Step(e2, s, T0.AddSeconds(5), run);
            T.Check("kapeyn run can be snoozed", !e2.Awake);
            e2.Unsnooze("cli:4242");
            Step(e2, s, T0.AddSeconds(10), run);
            T.Check("a new 'hold' unsnoozes it", e2.Awake);

            var both = R("call", "on a call", "net", "downloading");
            var e3 = new Engine("PC", false);
            Step(e3, s, T0, both);
            T.Eq("several reasons listed", "Awake - on a call, downloading", e3.StatusLine());
        }

        public static void WorkHoursEnd()
        {
            var s = S();
            s.ScheduleEnabled = true;

            var e = new Engine("PC", true);
            e.TurnOn(null, T0);
            Step(e, s, T0, inWindow: true);
            T.Check("in work hours: awake with the schedule reason", e.Awake && e.Reasons.Any(r => r.Key == "hours"));
            var ev = Step(e, s, T0.AddSeconds(5), inWindow: false);
            T.Check("end seen live: reported", ev.WorkHoursEnded);
            T.Check("end: Kapeyn was on by hand", ev.WasManualOn);
            T.Check("end: no lock or shutdown by default", !ev.Lock && !ev.Shutdown);
            T.Check("end: turned itself off", !e.ManualOn && !e.Awake);
            ev = Step(e, s, T0.AddSeconds(10), inWindow: false);
            T.Check("end is reported only once", !ev.WorkHoursEnded);

            s.EndLock = true;
            e = new Engine("PC", true);
            Step(e, s, T0, inWindow: true);
            ev = Step(e, s, T0.AddSeconds(5), inWindow: false);
            T.Check("'also lock' asks to lock", ev.Lock && !ev.Shutdown);

            s.EndShutdown = true;
            e = new Engine("PC", true);
            Step(e, s, T0, inWindow: true);
            ev = Step(e, s, T0.AddSeconds(5), inWindow: false);
            T.Check("'also shut down' wins over lock", ev.Shutdown && !ev.Lock);
            s.EndLock = false;
            s.EndShutdown = false;

            e = new Engine("PC", true);
            e.TurnOn(null, T0);
            Step(e, s, T0, inWindow: true);
            ev = Step(e, s, T0.AddMinutes(30), inWindow: false);
            T.Check("asleep across the end (30 min gap): nothing happens", !ev.WorkHoursEnded && e.ManualOn);

            e = new Engine("PC", true);
            Step(e, s, T0, inWindow: true);
            e.Rebaseline(false);
            ev = Step(e, s, T0.AddSeconds(5), inWindow: false);
            T.Check("after waking or saving new hours (rebaseline): nothing happens", !ev.WorkHoursEnded);

            var off = S();
            e = new Engine("PC", true);
            Step(e, off, T0, inWindow: true);
            ev = Step(e, off, T0.AddSeconds(5), inWindow: false);
            T.Check("schedule switched off: never fires", !ev.WorkHoursEnded);
            Step(e, off, T0.AddSeconds(10), inWindow: true);
            T.Check("schedule switched off: window alone doesn't keep it awake", !e.Awake);

            var keepOn = S();
            keepOn.ScheduleEnabled = true;
            keepOn.ScheduleEndTurnsOff = false;
            keepOn.EndShutdown = true;
            e = new Engine("PC", true);
            e.TurnOn(null, T0);
            Step(e, keepOn, T0, inWindow: true);
            ev = Step(e, keepOn, T0.AddSeconds(5), inWindow: false);
            T.Check("'Turn Kapeyn off' unticked: no action, stays on", !ev.WorkHoursEnded && !ev.Shutdown && e.Awake);

            e = new Engine("PC", false);
            Step(e, s, T0, inWindow: false);
            ev = Step(e, s, T0.AddSeconds(5), inWindow: true);
            T.Check("start of work hours: awake, no end event", e.Awake && !ev.WorkHoursEnded);
            T.Eq("status during work hours", "Awake - work hours", e.StatusLine());
        }

        public static void Battery()
        {
            var s = S();
            var e = new Engine("PC", false);
            e.TurnOn(null, T0);

            var ev = Step(e, s, T0, onBattery: true, pct: 15);
            T.Check("on battery below 20%: paused", e.Paused && !e.Awake);
            T.Check("pause is reported", ev.PauseStarted);
            T.Eq("status explains the pause", "Paused - battery at 15%", e.StatusLine());
            ev = Step(e, s, T0.AddSeconds(5), onBattery: true, pct: 15);
            T.Check("pause is reported only once", !ev.PauseStarted);

            e.TurnOn(null, T0.AddSeconds(10));
            Step(e, s, T0.AddSeconds(10), onBattery: true, pct: 14);
            T.Check("clicking the cup overrides the pause", e.Awake && !e.Paused);

            Step(e, s, T0.AddSeconds(15), onBattery: false, pct: 14);
            ev = Step(e, s, T0.AddSeconds(20), onBattery: true, pct: 14);
            T.Check("after plugging in and out again, it pauses again", e.Paused && ev.PauseStarted);

            Step(e, s, T0.AddSeconds(25), onBattery: true, pct: 25);
            T.Check("above the limit: awake", e.Awake);
            Step(e, s, T0.AddSeconds(30), onBattery: false, pct: 5);
            T.Check("plugged in: never paused", e.Awake);

            var never = S();
            never.PauseBatteryPercent = 0;
            var e2 = new Engine("PC", false);
            e2.TurnOn(null, T0);
            Step(e2, never, T0, onBattery: true, pct: 3);
            T.Check("'Never' setting: no pause even at 3%", e2.Awake);

            var e3 = new Engine("PC", false);
            Step(e3, s, T0, onBattery: true, pct: 5);
            T.Check("not paused when there's nothing to keep awake", !e3.Paused);
            T.Eq("Mac wording", "Off - Mac may sleep", new Engine("Mac", false).StatusLine());
        }
    }

    static class FormatTests
    {
        public static void Run()
        {
            T.Eq("45 s", "45 s", Format.Duration(45));
            T.Eq("1 min", "1 min", Format.Duration(60));
            T.Eq("1 min 30 s", "1 min 30 s", Format.Duration(90));
            T.Eq("2 min 30 s (auto nudge)", "2 min 30 s", Format.Duration(150));
            T.Eq("1 h", "1 h", Format.Duration(3600));
            T.Eq("1 h 30 min", "1 h 30 min", Format.Duration(5400));

            T.Eq("time left rounds up", "43 min", Format.Remaining(TimeSpan.FromMinutes(42.2)));
            T.Eq("under a minute shows 1 min", "1 min", Format.Remaining(TimeSpan.FromSeconds(20)));
            T.Eq("1 h 1 min", "1 h 1 min", Format.Remaining(TimeSpan.FromMinutes(61)));
            T.Eq("exact hours", "2 h", Format.Remaining(TimeSpan.FromHours(2)));

            T.Eq("menu: 30 minutes", "30 minutes", Format.Minutes(30));
            T.Eq("menu: 1 hour", "1 hour", Format.Minutes(60));
            T.Eq("menu: 8 hours", "8 hours", Format.Minutes(480));

            T.Eq("1 PM", "1:00 PM", Format.TimeOfDay(780));
            T.Eq("midnight", "12:00 AM", Format.TimeOfDay(0));
            T.Eq("9:30 AM", "9:30 AM", Format.TimeOfDay(570));

            T.Eq("Mon-Fri", "Mon-Fri", Format.Days(0x3E));
            T.Eq("every day", "every day", Format.Days(0x7F));
            T.Eq("weekend (Monday-first order)", "Sat, Sun", Format.Days(0x41));
            T.Eq("no days", "no days", Format.Days(0));

            var s = new Settings();
            T.Eq("schedule summary", "Mon-Fri, 9:00 AM - 5:00 PM", Format.Schedule(s));
            s.ScheduleStartMin = 22 * 60;
            s.ScheduleEndMin = 6 * 60;
            T.Eq("overnight summary", "Mon-Fri, 10:00 PM - 6:00 AM (next day)", Format.Schedule(s));
        }
    }

    static class DurationTests
    {
        static void Ok(string text, int minutes)
        {
            int m;
            bool ok = Cli.TryParseDuration(text, out m);
            T.Check("\"" + text + "\" = " + minutes + " min", ok && m == minutes, ok ? "got " + m : "not understood");
        }

        static void Bad(string text)
        {
            int m;
            T.Check("\"" + text + "\" is rejected", !Cli.TryParseDuration(text, out m));
        }

        public static void Run()
        {
            Ok("30m", 30);
            Ok("2h", 120);
            Ok("1h30m", 90);
            Ok("90", 90);
            Ok("45min", 45);
            Ok("2hrs", 120);
            Ok("1 hour 30 minutes", 90);
            Ok("1H30M", 90);
            Bad("");
            Bad("0");
            Bad("-5");
            Bad("banana");
            Bad("1h30");
            Bad("30s");
        }
    }

    static class SettingsTests
    {
        static string Temp(params string[] lines)
        {
            string dir = Path.Combine(Path.GetTempPath(), "kapeyn-tests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            string path = Path.Combine(dir, "settings.ini");
            if (lines.Length > 0) File.WriteAllLines(path, lines);
            return path;
        }

        public static void Run()
        {
            var fresh = Settings.LoadFrom(Temp());
            T.Check("no file: first run with defaults", fresh.FirstRun && fresh.StartActive && fresh.NudgeSeconds == 0
                && fresh.ScheduleStartMin == 540 && fresh.ScheduleEndMin == 1020 && fresh.PauseBatteryPercent == 20
                && !fresh.EndLock && !fresh.EndShutdown && fresh.ScheduleEndTurnsOff);

            var s = new Settings();
            s.StartActive = false; s.KeepScreenOn = false; s.SimulateActivity = false;
            s.Method = NudgeMethod.Both; s.NudgeSeconds = 120;
            s.WhileCall = false; s.WhileDownloading = false;
            s.WatchApps.Add(new WatchedApp { Name = "Zoom", Label = "Zoom Meetings" });
            s.WatchApps.Add(new WatchedApp { Name = "obs64", Label = "OBS; Studio | Pro" });
            s.ScheduleEnabled = true; s.ScheduleDays = 0x41; s.ScheduleStartMin = 1320; s.ScheduleEndMin = 360;
            s.ScheduleEndTurnsOff = false; s.EndLock = true;
            s.ScreenOffWhenLocked = false; s.PauseBatteryPercent = 30; s.Hotkey = false;
            string path = Temp();
            s.SaveTo(path);
            var r = Settings.LoadFrom(path);
            T.Check("round trip keeps every value", !r.FirstRun && !r.StartActive && !r.KeepScreenOn && !r.SimulateActivity
                && r.Method == NudgeMethod.Both && r.NudgeSeconds == 120 && !r.WhileCall && !r.WhileDownloading
                && r.ScheduleEnabled && r.ScheduleDays == 0x41 && r.ScheduleStartMin == 1320 && r.ScheduleEndMin == 360
                && !r.ScheduleEndTurnsOff && r.EndLock && !r.EndShutdown && !r.ScreenOffWhenLocked
                && r.PauseBatteryPercent == 30 && !r.Hotkey);
            T.Eq("watched apps survive", 2, r.WatchApps.Count);
            T.Eq("watched app name", "Zoom", r.WatchApps[0].Name);
            T.Eq("watched app label", "Zoom Meetings", r.WatchApps[0].Label);
            T.Check("separators in labels are cleaned, not corrupting the list",
                r.WatchApps[1].Name == "obs64" && !r.WatchApps[1].Label.Contains(";") && !r.WatchApps[1].Label.Contains("|"));

            var v1 = Settings.LoadFrom(Temp("StartActive=True", "NudgeSeconds=60"));
            T.Eq("v1 -> Auto nudge timing", 0, v1.NudgeSeconds);
            var v1Custom = Settings.LoadFrom(Temp("Version=2", "NudgeSeconds=60"));
            T.Eq("v2 keeps a chosen 60 s", 60, v1Custom.NudgeSeconds);

            var v2 = Settings.LoadFrom(Temp("Version=2", "ScheduleStart=12", "ScheduleEnd=22"));
            T.Check("v2 hours -> minutes (12 PM - 10 PM)", v2.ScheduleStartMin == 720 && v2.ScheduleEndMin == 1320);
            var both = Settings.LoadFrom(Temp("Version=3", "ScheduleStart=12", "ScheduleEnd=22", "ScheduleStartMin=780", "ScheduleEndMin=1320"));
            T.Eq("new minute keys win over old hour keys", 780, both.ScheduleStartMin);

            var bad = Settings.LoadFrom(Temp("NudgeSeconds=5", "ScheduleDays=999", "PauseBatteryPercent=150",
                                             "ScheduleStartMin=5000", "NudgeMethod=Banana", "Hotkey=maybe"));
            T.Check("invalid values are ignored", bad.NudgeSeconds == 0 && bad.ScheduleDays == (999 & 0x7F)
                && bad.PauseBatteryPercent == 20 && bad.ScheduleStartMin == 540 && bad.Method == NudgeMethod.Mouse && bad.Hotkey);

            var same = Settings.LoadFrom(Temp("Version=3", "ScheduleStartMin=600", "ScheduleEndMin=600"));
            T.Eq("same start and end is repaired (+8 h)", 1080, same.ScheduleEndMin);
            var lockAndShut = Settings.LoadFrom(Temp("Version=3", "EndLock=True", "EndShutdown=True"));
            T.Check("lock and shutdown together -> shutdown only", lockAndShut.EndShutdown && !lockAndShut.EndLock);
        }
    }

    static class NetMonitorTests
    {
        public static void Run()
        {
            DateTime t = new DateTime(2026, 11, 23, 10, 0, 0);
            long bytes = 0;
            const long Fast = 2 * 1024 * 1024; // 2 MB per 5-second sample = ~400 KB/s
            const long Slow = 10 * 1024;       // 10 KB per sample

            var n = new NetMonitor();
            T.Check("first reading is only a baseline", !n.Sample(bytes, t));

            bytes += Fast; t = t.AddSeconds(5);
            T.Check("one burst (e.g. a sync) doesn't count", !n.Sample(bytes, t));
            bytes += Slow; t = t.AddSeconds(5);
            T.Check("burst then quiet: still not downloading", !n.Sample(bytes, t));

            var d = new NetMonitor();
            t = new DateTime(2026, 11, 23, 11, 0, 0);
            bytes = 0;
            d.Sample(bytes, t);
            bool busy = false;
            for (int i = 0; i < 3; i++) { bytes += Fast; t = t.AddSeconds(5); busy = d.Sample(bytes, t); }
            T.Check("sustained traffic (3 of 4 samples) counts as downloading", busy);

            bytes += Slow; t = t.AddSeconds(2);
            T.Check("an early reading keeps the current answer", d.Sample(bytes, t));
            for (int i = 0; i < 10; i++) { bytes += Slow; t = t.AddSeconds(5); busy = d.Sample(bytes, t); }
            T.Check("stays busy for a minute after it stops", busy);
            for (int i = 0; i < 4; i++) { bytes += Slow; t = t.AddSeconds(5); busy = d.Sample(bytes, t); }
            T.Check("then stops", !busy);

            var r = new NetMonitor();
            t = new DateTime(2026, 11, 23, 12, 0, 0);
            r.Sample(5000000, t);
            T.Check("a counter reset (adapter reconnect) isn't a download", !r.Sample(1000, t.AddSeconds(5)));
        }
    }

    static class DoctorTests
    {
        static Finding F(int seconds, bool inputBased)
        {
            return new Finding { What = "x", Seconds = seconds, InputBased = inputBased, Kind = inputBased ? "lock" : "sleep" };
        }

        public static void Run()
        {
            T.Eq("nothing found: 2 min", 120, Doctor.RecommendedNudgeSeconds(new List<Finding>()));
            T.Eq("5-minute company lock: 2 min 30 s", 150, Doctor.RecommendedNudgeSeconds(new List<Finding> { F(300, true) }));
            T.Eq("very short lock: never below 30 s", 30, Doctor.RecommendedNudgeSeconds(new List<Finding> { F(40, true) }));
            T.Eq("long lock: capped at 4 min (under Teams' 5 min Away)", 240, Doctor.RecommendedNudgeSeconds(new List<Finding> { F(3600, true) }));
            T.Eq("only power timers: 2 min", 120, Doctor.RecommendedNudgeSeconds(new List<Finding> { F(60, false), F(300, false) }));
            T.Eq("uses the shortest input-based timer", 180,
                Doctor.RecommendedNudgeSeconds(new List<Finding> { F(900, true), F(360, true), F(60, false) }));
        }
    }

    static class HelpTests
    {
        static readonly string[] KnownTokens = { "computer", "Computer", "shortcut", "tray", "rightclick", "RightClick", "startup", "version" };

        public static void Run()
        {
            var topics = HelpContent.Load();
            T.Eq("11 topics", 11, topics.Count);
            T.Check("topic titles are unique", topics.Select(t => t.Title).Distinct().Count() == topics.Count);
            T.Check("every topic has text", topics.All(t => t.Lines.Any(l => l.Trim().Length > 0)));
            string all = string.Join("\n", topics.Select(t => t.Title + "\n" + string.Join("\n", t.Lines.ToArray())).ToArray());
            T.Check("no unfilled {tokens}", all.IndexOf('{') < 0);
            T.Check("no [win]/[mac] markers or comments left", !all.Contains("[win]") && !all.Contains("[mac]") && !all.Contains("%%"));
            T.Check("no Mac-only text on Windows", !all.Contains("Control-click") && !all.Contains("System Events"));
            T.Check("Windows wording is used", all.Contains("Ctrl+Alt+K") && all.Contains("Start with Windows") && all.Contains("PC"));
            T.Check("bold and code markers are balanced on every line", topics.SelectMany(t => t.Lines)
                .All(l => Regex.Matches(l, @"\*\*").Count % 2 == 0 && l.Count(c => c == '`') % 2 == 0));

            string raw = File.ReadAllText(Path.Combine(TestMain.RepoRoot, "help", "help.md"));
            var unknown = Regex.Matches(raw, @"\{(\w+)\}").Cast<Match>().Select(m => m.Groups[1].Value)
                               .Where(tk => !KnownTokens.Contains(tk)).Distinct().ToList();
            T.Check("help.md only uses known tokens", unknown.Count == 0, unknown.Count == 0 ? null : string.Join(", ", unknown.ToArray()));
            T.Check("every topic is shared by both platforms (no [win]/[mac] on '# ' lines)",
                !Regex.IsMatch(raw, @"^\[(win|mac)\] # ", RegexOptions.Multiline));
        }
    }
}
