using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Windows.Forms;
using Microsoft.Win32;

namespace Kapeyn
{
    enum NudgeMethod { Mouse, Keyboard, Both }

    sealed class WatchedApp
    {
        public string Name;   // process name, e.g. "Zoom"
        public string Label;  // friendly name, e.g. "Zoom Meetings"
    }

    // %APPDATA%\Kapeyn\settings.ini
    sealed class Settings
    {
        const int CurrentVersion = 2;

        public bool StartActive = true;
        public bool KeepScreenOn = true;
        public bool SimulateActivity = true;
        public NudgeMethod Method = NudgeMethod.Mouse;
        public int NudgeSeconds = 0;               // 0 = Auto (from the lock doctor's findings)

        public bool WhileCall = true;              // microphone in use
        public bool WhileDownloading = true;       // network busy
        public List<WatchedApp> WatchApps = new List<WatchedApp>();

        public bool ScheduleEnabled = false;
        public int ScheduleDays = 0x3E;            // bit per day, Sunday = bit 0 -> Mon..Fri
        public int ScheduleStart = 9;              // hour, inclusive
        public int ScheduleEnd = 17;               // hour, exclusive
        public bool ScheduleEndTurnsOff = true;

        public bool ScreenOffWhenLocked = true;
        public int PauseBatteryPercent = 20;       // 0 = never pause
        public bool Hotkey = true;

        public bool FirstRun;

        static string FolderPath
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Kapeyn"); }
        }

        static string FilePath
        {
            get { return Path.Combine(FolderPath, "settings.ini"); }
        }

        public static Settings Load()
        {
            var s = new Settings();
            try
            {
                if (!File.Exists(FilePath))
                {
                    s.FirstRun = true;
                    return s;
                }
                int version = 1;
                foreach (string raw in File.ReadAllLines(FilePath))
                {
                    int eq = raw.IndexOf('=');
                    if (eq <= 0) continue;
                    string key = raw.Substring(0, eq).Trim().ToLowerInvariant();
                    string val = raw.Substring(eq + 1).Trim();
                    bool b;
                    int n;
                    NudgeMethod nm;
                    switch (key)
                    {
                        case "version": if (int.TryParse(val, out n)) version = n; break;
                        case "startactive": if (bool.TryParse(val, out b)) s.StartActive = b; break;
                        case "keepscreenon": if (bool.TryParse(val, out b)) s.KeepScreenOn = b; break;
                        case "simulateactivity": if (bool.TryParse(val, out b)) s.SimulateActivity = b; break;
                        case "nudgemethod": if (Enum.TryParse(val, true, out nm)) s.Method = nm; break;
                        case "nudgeseconds":
                            if (int.TryParse(val, out n) && (n == 0 || (n >= 10 && n <= 3600))) s.NudgeSeconds = n;
                            break;
                        case "whilecall": if (bool.TryParse(val, out b)) s.WhileCall = b; break;
                        case "whiledownloading": if (bool.TryParse(val, out b)) s.WhileDownloading = b; break;
                        case "watchapps": s.WatchApps = ParseApps(val); break;
                        case "scheduleenabled": if (bool.TryParse(val, out b)) s.ScheduleEnabled = b; break;
                        case "scheduledays": if (int.TryParse(val, out n)) s.ScheduleDays = n & 0x7F; break;
                        case "schedulestart": if (int.TryParse(val, out n) && n >= 0 && n <= 23) s.ScheduleStart = n; break;
                        case "scheduleend": if (int.TryParse(val, out n) && n >= 1 && n <= 24) s.ScheduleEnd = n; break;
                        case "scheduleendturnsoff": if (bool.TryParse(val, out b)) s.ScheduleEndTurnsOff = b; break;
                        case "screenoffwhenlocked": if (bool.TryParse(val, out b)) s.ScreenOffWhenLocked = b; break;
                        case "pausebatterypercent": if (int.TryParse(val, out n) && n >= 0 && n <= 90) s.PauseBatteryPercent = n; break;
                        case "hotkey": if (bool.TryParse(val, out b)) s.Hotkey = b; break;
                    }
                }
                // v1 always saved NudgeSeconds=60 (its default). Move those users to Auto.
                if (version < 2 && s.NudgeSeconds == 60) s.NudgeSeconds = 0;
                if (s.ScheduleEnd <= s.ScheduleStart) s.ScheduleEnd = Math.Min(24, s.ScheduleStart + 8);
            }
            catch { }
            return s;
        }

        public void Save()
        {
            try
            {
                Directory.CreateDirectory(FolderPath);
                File.WriteAllLines(FilePath, new[]
                {
                    "Version=" + CurrentVersion,
                    "StartActive=" + StartActive,
                    "KeepScreenOn=" + KeepScreenOn,
                    "SimulateActivity=" + SimulateActivity,
                    "NudgeMethod=" + Method,
                    "NudgeSeconds=" + NudgeSeconds,
                    "WhileCall=" + WhileCall,
                    "WhileDownloading=" + WhileDownloading,
                    "WatchApps=" + FormatApps(WatchApps),
                    "ScheduleEnabled=" + ScheduleEnabled,
                    "ScheduleDays=" + ScheduleDays,
                    "ScheduleStart=" + ScheduleStart,
                    "ScheduleEnd=" + ScheduleEnd,
                    "ScheduleEndTurnsOff=" + ScheduleEndTurnsOff,
                    "ScreenOffWhenLocked=" + ScreenOffWhenLocked,
                    "PauseBatteryPercent=" + PauseBatteryPercent,
                    "Hotkey=" + Hotkey,
                });
            }
            catch { }
        }

        static List<WatchedApp> ParseApps(string val)
        {
            var list = new List<WatchedApp>();
            foreach (string entry in val.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string[] p = entry.Split('|');
                string name = p[0].Trim();
                if (name.Length == 0) continue;
                list.Add(new WatchedApp { Name = name, Label = p.Length > 1 && p[1].Trim().Length > 0 ? p[1].Trim() : name });
            }
            return list;
        }

        static string FormatApps(List<WatchedApp> apps)
        {
            var sb = new StringBuilder();
            foreach (var a in apps)
            {
                if (sb.Length > 0) sb.Append(';');
                sb.Append(Clean(a.Name)).Append('|').Append(Clean(a.Label));
            }
            return sb.ToString();
        }

        public static string Clean(string s)
        {
            return (s ?? "").Replace(";", " ").Replace("|", " ").Replace("\r", " ").Replace("\n", " ").Trim();
        }
    }

    // Start with Windows (HKCU\...\Run, no admin needed)
    static class Startup
    {
        const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        const string ValueName = "Kapeyn";

        static string Command
        {
            get { return "\"" + Application.ExecutablePath + "\""; }
        }

        public static bool IsEnabled()
        {
            using (var k = Registry.CurrentUser.OpenSubKey(RunKey))
                return k != null && k.GetValue(ValueName) != null;
        }

        public static void Set(bool enabled)
        {
            using (var k = Registry.CurrentUser.CreateSubKey(RunKey))
            {
                if (enabled) k.SetValue(ValueName, Command);
                else k.DeleteValue(ValueName, false);
            }
        }

        // If the exe was moved, point the startup entry at the new location.
        public static void RepairPath()
        {
            try
            {
                using (var k = Registry.CurrentUser.OpenSubKey(RunKey, true))
                {
                    if (k == null) return;
                    var current = k.GetValue(ValueName) as string;
                    if (current != null && !string.Equals(current, Command, StringComparison.OrdinalIgnoreCase))
                        k.SetValue(ValueName, Command);
                }
            }
            catch { }
        }
    }
}
