using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

namespace Kapeyn
{
    // One thing on this PC that can lock it, blank the screen or put it to sleep.
    sealed class Finding
    {
        public string What;
        public int Seconds;
        public bool InputBased;   // only simulated activity resets it (a power request doesn't)
        public bool ByOrg;        // set by IT policy
        public string Kind;      // "lock", "saver", "display", "sleep"
    }

    // "Lock doctor": finds what will lock or sleep this PC, and how Kapeyn covers each one.
    static class Doctor
    {
        static readonly Guid NoSubgroup = new Guid("fea3413e-7e05-4911-9a71-700331f1c294");
        static readonly Guid ConsoleLock = new Guid("0e796bdb-100d-47d6-a2d5-f7d2daa51f51");
        static readonly Guid VideoSubgroup = new Guid("7516b95f-f776-4464-8c53-06167f40cc99");
        static readonly Guid VideoIdle = new Guid("3c0bc021-c8a8-4e07-a973-6b14cbcb2b7e");
        static readonly Guid SleepSubgroup = new Guid("238c9fa8-0aad-41ed-83f4-97be242c8f20");
        static readonly Guid StandbyIdle = new Guid("29f6c1db-86da-48c5-9fdb-f2b67b1f44da");

        public static List<Finding> Scan()
        {
            var list = new List<Finding>();

            // 1. "Interactive logon: Machine inactivity limit" - locks after no input, whatever power requests say.
            int inactivity = ReadInt(Registry.LocalMachine, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System", "InactivityTimeoutSecs");
            if (inactivity > 0)
                list.Add(new Finding { Kind = "lock", What = "Windows locks after no keyboard/mouse input", Seconds = inactivity, InputBased = true, ByOrg = true });

            // 2. Screen saver (effective value, including policy).
            int active = 0, timeout = 0, secure = 0;
            Native.SystemParametersInfo(Native.SPI_GETSCREENSAVEACTIVE, 0, ref active, 0);
            Native.SystemParametersInfo(Native.SPI_GETSCREENSAVETIMEOUT, 0, ref timeout, 0);
            Native.SystemParametersInfo(Native.SPI_GETSCREENSAVESECURE, 0, ref secure, 0);
            if (active != 0 && timeout > 0)
            {
                bool policy = HasAnyValue(Registry.CurrentUser, @"Software\Policies\Microsoft\Windows\Control Panel\Desktop",
                                          "ScreenSaveActive", "ScreenSaveTimeOut", "ScreenSaverIsSecure", "SCRNSAVE.EXE");
                list.Add(new Finding
                {
                    Kind = "saver",
                    What = secure != 0 ? "Screen saver starts, then sign-in is required" : "Screen saver starts",
                    Seconds = timeout, InputBased = true, ByOrg = policy,
                });
            }

            // 3. Power plan: display off and sleep (policy overrides win if present).
            bool pluggedIn = SystemInformation.PowerStatus.PowerLineStatus != PowerLineStatus.Offline;
            string plan = pluggedIn ? "plugged in" : "on battery";
            bool byOrg;
            int consoleLock = PowerValue(pluggedIn, NoSubgroup, ConsoleLock, out byOrg);
            int video = PowerValue(pluggedIn, VideoSubgroup, VideoIdle, out byOrg);
            if (video > 0)
                list.Add(new Finding
                {
                    Kind = "display",
                    What = "Display turns off (" + plan + ")" + (consoleLock == 1 ? ", then sign-in is required" : ""),
                    Seconds = video, InputBased = false, ByOrg = byOrg,
                });
            int sleep = PowerValue(pluggedIn, SleepSubgroup, StandbyIdle, out byOrg);
            if (sleep > 0)
                list.Add(new Finding { Kind = "sleep", What = "PC goes to sleep (" + plan + ")", Seconds = sleep, InputBased = false, ByOrg = byOrg });

            return list.OrderBy(f => f.Seconds).ToList();
        }

        /// Nudge a bit before the shortest input-based timer fires (and under Teams' 5-minute "Away").
        public static int RecommendedNudgeSeconds(List<Finding> findings)
        {
            int min = int.MaxValue;
            foreach (var f in findings)
                if (f.InputBased && f.Seconds > 0) min = Math.Min(min, f.Seconds);
            if (min == int.MaxValue) return 120;
            return Math.Max(30, Math.Min(240, min / 2));
        }

        public static string Report(Settings s, bool awake, string status, int nudgeSeconds, bool autoNudge, bool runSelfTest)
        {
            var findings = Scan();
            var problems = new List<string>();
            var sb = new StringBuilder();

            sb.AppendLine("KAPEYN LOCK DOCTOR");
            sb.AppendLine("==================");
            sb.AppendLine();
            sb.AppendLine("Timers on this PC that can lock it, blank the screen or put it to sleep:");
            if (findings.Count == 0) sb.AppendLine("  (none found - nothing here locks or sleeps this PC on a timer)");
            foreach (var f in findings)
            {
                sb.AppendLine();
                sb.AppendLine("  * " + f.What + ": after " + Format.Duration(f.Seconds) + (f.ByOrg ? "   [set by your organization]" : ""));
                sb.AppendLine("      " + Coverage(f, s, nudgeSeconds, problems));
            }

            sb.AppendLine();
            sb.AppendLine("Kapeyn right now:");
            sb.AppendLine("  * Status: " + status);
            uint es = Native.SystemExecutionState();
            sb.AppendLine("  * Windows sees a keep-awake request: " +
                ((es & Native.ES_SYSTEM_REQUIRED) != 0 ? "yes (PC stays awake" + ((es & Native.ES_DISPLAY_REQUIRED) != 0 ? ", display stays on)" : ")") : "no"));
            sb.AppendLine("  * Activity simulation: " + (s.SimulateActivity
                ? "on - nudges after " + Format.Duration(nudgeSeconds) + " of idle" + (autoNudge ? " (Auto: picked from the timers above)" : "")
                : "off"));
            if (runSelfTest)
            {
                uint sent = Native.Nudge(s.Method);
                int expected = Native.NudgeEventCount(s.Method);
                sb.AppendLine("  * Self-test: Windows accepted " + sent + " of " + expected + " simulated inputs " + (sent == expected ? "[OK]" : "[!]"));
                if (sent != expected) problems.Add("Windows blocked Kapeyn's simulated input. Try Settings > Activity method > Both, or check with IT whether input simulation is blocked.");
            }

            if (!awake) problems.Insert(0, "Kapeyn is off right now, so none of this protection is active. Click the cup to turn it on.");

            sb.AppendLine();
            sb.AppendLine("Verdict:");
            if (problems.Count == 0) sb.AppendLine("  [OK] Kapeyn covers every timer found on this PC.");
            foreach (var p in problems) sb.AppendLine("  [!] " + p);
            return sb.ToString();
        }

        static string Coverage(Finding f, Settings s, int nudge, List<string> problems)
        {
            if (f.InputBased)
            {
                if (!s.SimulateActivity)
                {
                    problems.Add("\"" + f.What + "\" only resets with keyboard/mouse activity. Turn on Settings > Prevent screen lock & Away status.");
                    return "[!] Not covered: only simulated activity prevents this, and it's off.";
                }
                if (nudge >= f.Seconds)
                {
                    problems.Add("Kapeyn nudges after " + Format.Duration(nudge) + ", which is too late for \"" + f.What + "\". Set Settings > Nudge when idle for > Auto.");
                    return "[!] Not covered: Kapeyn's nudge comes too late.";
                }
                return "[OK] Covered: Kapeyn nudges after " + Format.Duration(nudge) + " of idle.";
            }
            if (f.Kind == "display")
            {
                if (s.KeepScreenOn || s.SimulateActivity) return "[OK] Covered: Kapeyn keeps the display on.";
                return "[i] Kapeyn lets the display turn off (Settings > Keep screen on is off).";
            }
            return "[OK] Covered: Kapeyn's keep-awake request blocks sleep.";
        }

        static int ReadInt(RegistryKey hive, string path, string name)
        {
            try
            {
                using (var k = hive.OpenSubKey(path))
                {
                    if (k == null) return 0;
                    object v = k.GetValue(name);
                    if (v is int) return (int)v;
                    int n;
                    return v != null && int.TryParse(v.ToString(), out n) ? n : 0;
                }
            }
            catch { return 0; }
        }

        static bool HasAnyValue(RegistryKey hive, string path, params string[] names)
        {
            try
            {
                using (var k = hive.OpenSubKey(path))
                {
                    if (k == null) return false;
                    foreach (var n in names) if (k.GetValue(n) != null) return true;
                }
            }
            catch { }
            return false;
        }

        // Power plan value, preferring a Group Policy override when one exists.
        static int PowerValue(bool pluggedIn, Guid subgroup, Guid setting, out bool byOrg)
        {
            byOrg = false;
            try
            {
                using (var k = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Policies\Microsoft\Power\PowerSettings\" + setting.ToString("B").ToUpperInvariant()))
                {
                    if (k != null)
                    {
                        object v = k.GetValue(pluggedIn ? "ACSettingIndex" : "DCSettingIndex");
                        if (v is int) { byOrg = true; return (int)v; }
                    }
                }
            }
            catch { }
            return Native.ReadPowerSetting(pluggedIn, subgroup, setting);
        }
    }

    // Shows a report in a resizable window with Copy / Close.
    sealed class ReportForm : Form
    {
        public ReportForm(string title, string text)
        {
            Text = title;
            StartPosition = FormStartPosition.CenterScreen;
            MinimizeBox = false;
            ShowIcon = true;
            Icon = IconArt.CreateIcon(32, true);

            float scale;
            using (var g = CreateGraphics()) scale = g.DpiX / 96f;
            ClientSize = new Size((int)(760 * scale), (int)(540 * scale));

            string body = text.Replace("\r\n", "\n").Replace("\n", "\r\n");
            var box = new TextBox
            {
                Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, WordWrap = true,
                Dock = DockStyle.Fill, Font = new Font("Consolas", 10f), Text = body,
                BackColor = SystemColors.Window, BorderStyle = BorderStyle.None,
            };

            var buttons = new FlowLayoutPanel
            {
                Dock = DockStyle.Bottom, FlowDirection = FlowDirection.RightToLeft, AutoSize = true,
                Padding = new Padding((int)(8 * scale)),
            };
            var close = new Button { Text = "Close", AutoSize = true, DialogResult = DialogResult.OK };
            var copy = new Button { Text = "Copy", AutoSize = true };
            copy.Click += delegate { try { Clipboard.SetText(body); copy.Text = "Copied"; } catch { } };
            buttons.Controls.Add(close);
            buttons.Controls.Add(copy);

            Controls.Add(box);
            Controls.Add(buttons);
            AcceptButton = close;
            CancelButton = close;
            Shown += delegate { box.SelectionStart = 0; box.SelectionLength = 0; close.Focus(); };
        }
    }

    static class Format
    {
        public static string Duration(int seconds)
        {
            if (seconds < 60) return seconds + " s";
            if (seconds < 3600)
            {
                int m = seconds / 60, s = seconds % 60;
                return s == 0 ? m + " min" : m + " min " + s + " s";
            }
            int h = seconds / 3600, mm = (seconds % 3600) / 60;
            return mm == 0 ? h + " h" : h + " h " + mm + " min";
        }

        public static string Remaining(TimeSpan r)
        {
            int totalMin = Math.Max(1, (int)Math.Ceiling(r.TotalMinutes));
            int h = totalMin / 60, m = totalMin % 60;
            if (h == 0) return totalMin + " min";
            return m == 0 ? h + " h" : h + " h " + m + " min";
        }

        public static string Minutes(int minutes)
        {
            if (minutes < 60) return minutes + " minutes";
            int h = minutes / 60;
            return h == 1 ? "1 hour" : h + " hours";
        }

        public static string Hour(int hour)
        {
            if (hour == 24) return "midnight";
            return new DateTime(2000, 1, 1, hour, 0, 0).ToString("t");
        }

        public static string Ago(DateTime when)
        {
            if (when == DateTime.MinValue) return "not yet";
            int s = (int)Math.Max(0, (DateTime.Now - when).TotalSeconds);
            return Duration(s) + " ago";
        }

        public static string Days(int mask)
        {
            if (mask == 0x3E) return "Mon-Fri";
            if (mask == 0x7F) return "every day";
            var names = new[] { "Sun", "Mon", "Tue", "Wed", "Thu", "Fri", "Sat" };
            var on = new List<string>();
            for (int i = 1; i <= 7; i++) { int d = i % 7; if ((mask & (1 << d)) != 0) on.Add(names[d]); }
            return on.Count == 0 ? "no days" : string.Join(", ", on.ToArray());
        }
    }
}
