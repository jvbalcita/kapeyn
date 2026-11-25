using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net.NetworkInformation;
using System.Windows.Forms;
using Microsoft.Win32;

namespace Kapeyn
{
    // Things that can keep the PC awake automatically.
    static class Triggers
    {
        // ---- work hours ----------------------------------------------------------------------
        /// True inside the work-hours window (ignores whether the schedule is switched on).
        /// A window that ends earlier than it starts runs overnight and belongs to the day it started.
        public static bool InWorkHours(Settings s, DateTime now)
        {
            int t = now.Hour * 60 + now.Minute;
            int start = s.ScheduleStartMin, end = s.ScheduleEndMin;
            if (start < end) return DayOn(s, now.DayOfWeek) && t >= start && t < end;
            return (t >= start && DayOn(s, now.DayOfWeek)) || (t < end && DayOn(s, now.AddDays(-1).DayOfWeek));
        }

        static bool DayOn(Settings s, DayOfWeek d)
        {
            return (s.ScheduleDays & (1 << (int)d)) != 0;
        }

        // ---- on a call (microphone in use) -----------------------------------------------------
        // Windows records microphone use per app for the privacy indicator. An app that is using the
        // mic right now has LastUsedTimeStart set and LastUsedTimeStop = 0. Readable without admin.
        const string MicKey = @"Software\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\microphone";

        public static bool MicInUse(out string who)
        {
            who = null;
            try
            {
                using (var root = Registry.CurrentUser.OpenSubKey(MicKey))
                {
                    if (root == null) return false;
                    foreach (string sub in root.GetSubKeyNames())
                    {
                        using (var k = root.OpenSubKey(sub))
                        {
                            if (k == null) continue;
                            if (sub == "NonPackaged")
                            {
                                foreach (string app in k.GetSubKeyNames())
                                {
                                    using (var a = k.OpenSubKey(app))
                                    {
                                        if (a != null && InUse(a)) { who = FriendlyFromPath(app); return true; }
                                    }
                                }
                            }
                            else if (InUse(k))
                            {
                                who = FriendlyFromPackage(sub);
                                return true;
                            }
                        }
                    }
                }
            }
            catch { }
            return false;
        }

        static bool InUse(RegistryKey k)
        {
            object start = k.GetValue("LastUsedTimeStart");
            object stop = k.GetValue("LastUsedTimeStop");
            return start is long && (long)start > 0 && stop is long && (long)stop == 0;
        }

        // "C:#Program Files#Zoom#bin#Zoom.exe" -> "Zoom"
        static string FriendlyFromPath(string key)
        {
            string last = key.Substring(key.LastIndexOf('#') + 1);
            if (last.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) last = last.Substring(0, last.Length - 4);
            return last;
        }

        // "MSTeams_8wekyb3d8bbwe" -> "MSTeams"
        static string FriendlyFromPackage(string key)
        {
            int u = key.IndexOf('_');
            string name = u > 0 ? key.Substring(0, u) : key;
            int dot = name.LastIndexOf('.');
            return dot >= 0 && dot < name.Length - 1 ? name.Substring(dot + 1) : name;
        }

        // ---- watched apps --------------------------------------------------------------------
        public static bool IsRunning(string processName)
        {
            Process[] ps = Process.GetProcessesByName(processName);
            bool running = ps.Length > 0;
            foreach (var p in ps) p.Dispose();
            return running;
        }

        static readonly HashSet<string> Boring = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "explorer", "ApplicationFrameHost", "TextInputHost", "ShellExperienceHost", "StartMenuExperienceHost",
            "SearchHost", "SystemSettings", "LockApp", "Kapeyn",
        };

        /// Apps that currently have a window, for the "Add a running app" menu.
        public static List<WatchedApp> RunningWindowApps()
        {
            var byName = new Dictionary<string, WatchedApp>(StringComparer.OrdinalIgnoreCase);
            int self = Process.GetCurrentProcess().Id;
            foreach (var p in Process.GetProcesses())
            {
                try
                {
                    if (p.Id == self || p.MainWindowHandle == IntPtr.Zero) continue;
                    string name = p.ProcessName;
                    if (Boring.Contains(name) || byName.ContainsKey(name)) continue;
                    string label = name;
                    try
                    {
                        string d = p.MainModule.FileVersionInfo.FileDescription;
                        if (!string.IsNullOrEmpty(d) && d.Trim().Length > 0) label = d.Trim();
                        // Some apps describe themselves as "Notepad.exe"; show "Notepad".
                        if (label.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && label.Length > 4)
                            label = label.Substring(0, label.Length - 4);
                    }
                    catch { }
                    byName[name] = new WatchedApp { Name = name, Label = Settings.Clean(label) };
                }
                catch { }
                finally { p.Dispose(); }
            }
            return byName.Values.OrderBy(a => a.Label, StringComparer.CurrentCultureIgnoreCase).ToList();
        }

        // ---- command-line holds --------------------------------------------------------------
        public static bool IsAlive(int pid)
        {
            try
            {
                using (var p = Process.GetProcessById(pid)) return !p.HasExited;
            }
            catch { return false; }
        }

        // ---- battery -------------------------------------------------------------------------
        /// True when running on battery. percent = 0..100, or -1 if unknown.
        public static bool OnBattery(out int percent)
        {
            var ps = SystemInformation.PowerStatus;
            percent = ps.BatteryLifePercent >= 0f && ps.BatteryLifePercent <= 1f
                ? (int)Math.Round(ps.BatteryLifePercent * 100)
                : -1;
            return ps.PowerLineStatus == PowerLineStatus.Offline
                && (ps.BatteryChargeStatus & BatteryChargeStatus.NoSystemBattery) == 0;
        }
    }

    // "Downloading or streaming": total bytes received across network adapters, sampled about every 5 s.
    // Busy when 3 of the last 4 samples are above ~150 KB/s (so one-off sync bursts don't count),
    // and stays busy for a minute after it drops (so short pauses don't flap).
    sealed class NetMonitor
    {
        const double Threshold = 150 * 1024;
        readonly Queue<bool> recent = new Queue<bool>();
        long lastBytes = -1;
        DateTime lastTime;
        DateTime busyUntil = DateTime.MinValue;

        public double BytesPerSecond { get; private set; }

        public bool Update()
        {
            long total = 0;
            try
            {
                foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (ni.OperationalStatus != OperationalStatus.Up) continue;
                    if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback ||
                        ni.NetworkInterfaceType == NetworkInterfaceType.Tunnel) continue;
                    try { total += ni.GetIPStatistics().BytesReceived; } catch { }
                }
            }
            catch { return DateTime.UtcNow < busyUntil; }
            return Sample(total, DateTime.UtcNow);
        }

        /// Feeds one reading of total bytes received. Returns whether downloading counts as active.
        public bool Sample(long total, DateTime now)
        {
            if (lastBytes < 0)
            {
                lastBytes = total;
                lastTime = now;
                return false;
            }
            double secs = (now - lastTime).TotalSeconds;
            if (secs < 4) return now < busyUntil; // called early (menu/command): keep the current answer

            BytesPerSecond = Math.Max(0, (total - lastBytes) / secs);
            lastBytes = total;
            lastTime = now;

            recent.Enqueue(BytesPerSecond >= Threshold);
            while (recent.Count > 4) recent.Dequeue();
            if (recent.Count(b => b) >= 3) busyUntil = now.AddMinutes(1);
            return now < busyUntil;
        }
    }
}
