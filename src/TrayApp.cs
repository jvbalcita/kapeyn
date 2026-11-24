using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

namespace Kapeyn
{
    // One reason to stay awake. Key identifies it (for snoozing), Label is what the user sees.
    sealed class Reason
    {
        public readonly string Key;
        public readonly string Label;
        public Reason(string key, string label) { Key = key; Label = label; }
    }

    sealed class TrayApp : ApplicationContext
    {
        static readonly int[] DurationMinutes = { 15, 30, 60, 120, 240, 480 };
        static readonly int[] NudgeChoices = { 30, 60, 120, 240 };
        static readonly int[] BatteryChoices = { 10, 20, 30 };
        static readonly string[] DayNames = { "Sunday", "Monday", "Tuesday", "Wednesday", "Thursday", "Friday", "Saturday" };
        const string HotkeyText = "Ctrl+Alt+K";

        readonly Settings settings;
        readonly Control invoker;
        readonly NotifyIcon tray;
        readonly ContextMenuStrip menu;
        readonly System.Windows.Forms.Timer tick;
        readonly RegisteredWaitHandle secondLaunchWait;
        readonly Icon iconOn, iconOff;
        readonly NetMonitor net = new NetMonitor();
        readonly HotkeyWindow hotkey = new HotkeyWindow();
        readonly IpcServer ipc;

        // ---- state ---------------------------------------------------------------------------
        bool manualOn;
        DateTime? manualUntil;
        readonly Dictionary<int, string> holds = new Dictionary<int, string>(); // kapeyn run: pid -> label
        readonly HashSet<string> snoozed = new HashSet<string>();               // auto reasons the user turned off
        List<Reason> lastAuto = new List<Reason>();
        List<Reason> reasons = new List<Reason>();
        bool awake, paused, locked, wasInWorkHours, batteryOverride, batteryNotified, hotkeyOk;
        string pauseWhy;
        DateTime lastNudge = DateTime.MinValue;
        int autoNudgeSeconds = 120;
        DateTime lastPolicyScan = DateTime.MinValue;

        // ---- menu items ----------------------------------------------------------------------
        ToolStripMenuItem statusItem, detailItem, toggleItem, durationMenu;
        ToolStripMenuItem callItem, netItem, appsMenu, scheduleItem, hoursMenu, daysMenu, startMenu, endMenu, endOffItem;
        ToolStripMenuItem screenItem, activityItem, methodMenu, intervalMenu, lockedItem, batteryMenu,
                          hotkeyItem, startActiveItem, startupItem;

        public TrayApp(EventWaitHandle secondLaunchSignal, bool cliLaunch)
        {
            settings = Settings.Load();

            invoker = new Control();
            IntPtr forceHandle = invoker.Handle; // create the window handle so Invoke works
            GC.KeepAlive(forceHandle);

            int size = SystemInformation.SmallIconSize.Width;
            iconOn = IconArt.CreateIcon(size, true);
            iconOff = IconArt.CreateIcon(size, false);

            menu = BuildMenu();
            menu.Opening += delegate { RefreshMenu(); };

            tray = new NotifyIcon();
            tray.ContextMenuStrip = menu;
            tray.Icon = iconOff;
            tray.Text = "Kapeyn";
            tray.Visible = true;
            tray.MouseClick += delegate(object s, MouseEventArgs e)
            {
                if (e.Button == MouseButtons.Left) Toggle();
            };

            hotkey.Pressed += delegate
            {
                Toggle();
                Notify(awake ? "Kapeyn is on" : "Kapeyn is off", StatusLine());
            };
            ApplyHotkey();

            tick = new System.Windows.Forms.Timer();
            tick.Interval = 5000;
            tick.Tick += delegate { Evaluate(true); };
            tick.Start();

            secondLaunchWait = ThreadPool.RegisterWaitForSingleObject(secondLaunchSignal,
                delegate { invoker.BeginInvoke(new Action(OnSecondLaunch)); }, null, -1, false);

            SystemEvents.PowerModeChanged += OnPowerModeChanged;
            SystemEvents.SessionSwitch += OnSessionSwitch;

            ipc = new IpcServer(invoker, HandleCommand);
            ipc.Start();

            Startup.RepairPath();
            RescanPolicies();

            wasInWorkHours = settings.ScheduleEnabled && Triggers.InWorkHours(settings, DateTime.Now);
            if (settings.StartActive && !cliLaunch) manualOn = true;
            Evaluate(false);

            if (settings.FirstRun)
            {
                settings.Save();
                if (!cliLaunch)
                    Notify("Kapeyn is running",
                        "Your PC will stay awake. Click the cup in the tray to turn it on/off, right-click for options, or press " + HotkeyText + ".");
            }
        }

        int EffectiveNudgeSeconds
        {
            get { return settings.NudgeSeconds > 0 ? settings.NudgeSeconds : autoNudgeSeconds; }
        }

        // ---- the engine ----------------------------------------------------------------------

        void Evaluate(bool allowNudge)
        {
            DateTime now = DateTime.Now;
            if ((now - lastPolicyScan).TotalMinutes >= 10) RescanPolicies();

            if (manualOn && manualUntil.HasValue && now >= manualUntil.Value)
            {
                manualOn = false;
                manualUntil = null;
                Notify("Kapeyn", "Timer finished. Your PC can sleep again.");
            }

            bool inHours = settings.ScheduleEnabled && Triggers.InWorkHours(settings, now);
            if (wasInWorkHours && !inHours && settings.ScheduleEndTurnsOff && manualOn)
            {
                manualOn = false;
                manualUntil = null;
                Notify("Kapeyn", "Work hours are over, so Kapeyn turned itself off.");
            }
            wasInWorkHours = inHours;

            // Automatic reasons
            var auto = new List<Reason>();
            if (inHours) auto.Add(new Reason("hours", "work hours"));
            if (settings.WhileCall)
            {
                string who;
                if (Triggers.MicInUse(out who))
                    auto.Add(new Reason("call", who == null ? "on a call" : "on a call (" + who + ")"));
            }
            if (settings.WhileDownloading && net.Update()) auto.Add(new Reason("net", "downloading"));
            foreach (var app in settings.WatchApps)
                if (Triggers.IsRunning(app.Name))
                    auto.Add(new Reason("app:" + app.Name.ToLowerInvariant(), app.Label + " is open"));
            foreach (int pid in holds.Keys.ToList())
            {
                if (!Triggers.IsAlive(pid)) holds.Remove(pid);
                else auto.Add(new Reason("cli:" + pid, "running " + holds[pid]));
            }
            lastAuto = auto;

            // Forget snoozes for reasons that have ended, so the next call/download wakes Kapeyn again.
            var keys = new HashSet<string>(auto.Select(r => r.Key));
            snoozed.RemoveWhere(k => !keys.Contains(k));

            var active = new List<Reason>();
            if (manualOn)
                active.Add(new Reason("manual", manualUntil.HasValue ? Format.Remaining(manualUntil.Value - now) + " left" : "turned on"));
            active.AddRange(auto.Where(r => !snoozed.Contains(r.Key)));
            reasons = active;
            bool wanted = active.Count > 0;

            // Low battery pause
            paused = false;
            pauseWhy = null;
            int pct;
            bool onBattery = Triggers.OnBattery(out pct);
            if (!onBattery) batteryOverride = false;
            if (wanted && settings.PauseBatteryPercent > 0 && onBattery && pct >= 0 &&
                pct < settings.PauseBatteryPercent && !batteryOverride)
            {
                paused = true;
                pauseWhy = "battery at " + pct + "%";
                if (!batteryNotified)
                {
                    batteryNotified = true;
                    Notify("Kapeyn paused", "Battery is at " + pct + "%, so your PC may sleep. Click the cup to keep it awake anyway.");
                }
            }
            if (!paused) batteryNotified = false;

            awake = wanted && !paused;
            ApplyPowerRequest();

            if (awake && allowNudge && settings.SimulateActivity && !locked)
            {
                int n = EffectiveNudgeSeconds - 2;
                if (Native.GetIdleTime().TotalSeconds >= n && (now - lastNudge).TotalSeconds >= n)
                {
                    Native.Nudge(settings.Method);
                    lastNudge = now;
                }
            }

            UpdateUi();
        }

        void ApplyPowerRequest()
        {
            if (!awake)
            {
                Native.SetThreadExecutionState(Native.ES_CONTINUOUS);
                return;
            }
            uint flags = Native.ES_CONTINUOUS | Native.ES_SYSTEM_REQUIRED;
            bool screenMayGoOff = locked && settings.ScreenOffWhenLocked;
            if (settings.KeepScreenOn && !screenMayGoOff) flags |= Native.ES_DISPLAY_REQUIRED;
            Native.SetThreadExecutionState(flags);
        }

        void RescanPolicies()
        {
            lastPolicyScan = DateTime.Now;
            try { autoNudgeSeconds = Doctor.RecommendedNudgeSeconds(Doctor.Scan()); }
            catch { autoNudgeSeconds = 120; }
        }

        void TurnOn(int? minutes)
        {
            manualOn = true;
            manualUntil = minutes.HasValue ? DateTime.Now.AddMinutes(minutes.Value) : (DateTime?)null;
            snoozed.Clear();
            batteryOverride = paused || batteryOverride;
            lastNudge = DateTime.MinValue;
            Evaluate(false);
        }

        void TurnOff()
        {
            manualOn = false;
            manualUntil = null;
            foreach (var r in lastAuto) snoozed.Add(r.Key);
            batteryOverride = false;
            Evaluate(false);
        }

        void Toggle()
        {
            if (awake) TurnOff();
            else TurnOn(null);
        }

        // ---- events --------------------------------------------------------------------------

        void OnPowerModeChanged(object sender, PowerModeChangedEventArgs e)
        {
            if (e.Mode == PowerModes.Resume || e.Mode == PowerModes.StatusChange)
                invoker.BeginInvoke(new Action(delegate { Evaluate(false); }));
        }

        void OnSessionSwitch(object sender, SessionSwitchEventArgs e)
        {
            bool? nowLocked = null;
            if (e.Reason == SessionSwitchReason.SessionLock) nowLocked = true;
            else if (e.Reason == SessionSwitchReason.SessionUnlock) nowLocked = false;
            if (nowLocked.HasValue)
            {
                bool value = nowLocked.Value;
                invoker.BeginInvoke(new Action(delegate { locked = value; Evaluate(false); }));
            }
        }

        void OnSecondLaunch()
        {
            if (!awake) TurnOn(null);
            Notify("Kapeyn is already running",
                "Your PC is being kept awake. Find the cup in the system tray (click ^ on the taskbar if it's hidden).");
        }

        // Commands from the "kapeyn" command line tool (runs on the UI thread).
        string HandleCommand(string line)
        {
            string[] parts = (line ?? "").Trim().Split(new[] { ' ' }, 3, StringSplitOptions.RemoveEmptyEntries);
            string verb = parts.Length > 0 ? parts[0].ToLowerInvariant() : "status";
            int n;
            switch (verb)
            {
                case "ping":
                    return "pong";
                case "on":
                    if (parts.Length > 1 && int.TryParse(parts[1], out n) && n > 0) TurnOn(n);
                    else TurnOn(null);
                    return "Kapeyn: " + StatusLine();
                case "off":
                    TurnOff();
                    return "Kapeyn: " + StatusLine();
                case "toggle":
                    Toggle();
                    return "Kapeyn: " + StatusLine();
                case "status":
                    Evaluate(false);
                    return StatusReport();
                case "hold":
                    if (parts.Length > 1 && int.TryParse(parts[1], out n))
                    {
                        holds[n] = parts.Length > 2 ? parts[2] : "a command";
                        snoozed.Remove("cli:" + n);
                        Evaluate(false);
                        return "ok";
                    }
                    return "error: expected hold <pid> <label>";
                case "release":
                    if (parts.Length > 1 && int.TryParse(parts[1], out n))
                    {
                        holds.Remove(n);
                        Evaluate(false);
                        return "ok";
                    }
                    return "error: expected release <pid>";
                case "doctor":
                    return Doctor.Report(settings, awake, StatusLine(), EffectiveNudgeSeconds, settings.NudgeSeconds == 0, true);
                default:
                    return "error: unknown command \"" + verb + "\"";
            }
        }

        void Exit()
        {
            tick.Stop();
            ipc.Stop();
            secondLaunchWait.Unregister(null);
            SystemEvents.PowerModeChanged -= OnPowerModeChanged;
            SystemEvents.SessionSwitch -= OnSessionSwitch;
            hotkey.Dispose();
            Native.SetThreadExecutionState(Native.ES_CONTINUOUS);
            tray.Visible = false;
            tray.Dispose();
            menu.Dispose();
            invoker.Dispose();
            ExitThread();
        }

        // ---- text ----------------------------------------------------------------------------

        string StatusLine()
        {
            if (paused) return "Paused - " + pauseWhy;
            if (!awake) return "Off - PC may sleep";
            // Just "Awake" when the only reason is that the user turned it on.
            if (reasons.Count == 1 && reasons[0].Key == "manual" && !manualUntil.HasValue) return "Awake";
            return "Awake - " + string.Join(", ", reasons.Select(r => r.Label).ToArray());
        }

        string DetailLine()
        {
            if (!awake) return null;
            if (!settings.SimulateActivity) return "Screen lock prevention is off";
            if (locked) return "Screen is locked - not nudging";
            return "Last nudge " + Format.Ago(lastNudge) + " - idle " + Format.Duration((int)Native.GetIdleTime().TotalSeconds);
        }

        string StatusReport()
        {
            var sb = new StringBuilder();
            sb.AppendLine("Kapeyn: " + StatusLine());
            string detail = DetailLine();
            if (detail != null) sb.AppendLine("  " + detail);
            sb.AppendLine("  Keep screen on: " + YesNo(settings.KeepScreenOn) +
                          " | Prevent screen lock: " + (settings.SimulateActivity
                              ? "yes, nudge after " + Format.Duration(EffectiveNudgeSeconds) + " idle" + (settings.NudgeSeconds == 0 ? " (auto)" : "")
                              : "no"));
            sb.AppendLine("  Automatic: calls " + YesNo(settings.WhileCall) +
                          " | downloads " + YesNo(settings.WhileDownloading) +
                          " | work hours " + (settings.ScheduleEnabled ? Format.Days(settings.ScheduleDays) + " " + Format.Hour(settings.ScheduleStart) + "-" + Format.Hour(settings.ScheduleEnd) : "no") +
                          (settings.WatchApps.Count > 0 ? " | apps " + string.Join(", ", settings.WatchApps.Select(a => a.Label).ToArray()) : ""));
            return sb.ToString();
        }

        static string YesNo(bool b) { return b ? "yes" : "no"; }

        void UpdateUi()
        {
            tray.Icon = awake ? iconOn : iconOff;
            string text = "Kapeyn: " + StatusLine();
            tray.Text = text.Length > 63 ? text.Substring(0, 60) + "..." : text;
        }

        void Notify(string title, string text)
        {
            try { tray.ShowBalloonTip(4000, title, text, ToolTipIcon.Info); } catch { }
        }

        void ApplyHotkey()
        {
            hotkey.Unregister();
            hotkeyOk = !settings.Hotkey || hotkey.Register();
        }

        // ---- menu ----------------------------------------------------------------------------

        static ToolStripMenuItem Item(string text, EventHandler onClick)
        {
            return new ToolStripMenuItem(text, null, onClick);
        }

        static ToolStripMenuItem Info(string text)
        {
            var i = new ToolStripMenuItem(text);
            i.Enabled = false;
            return i;
        }

        void Changed()
        {
            settings.Save();
            Evaluate(false);
        }

        ContextMenuStrip BuildMenu()
        {
            var m = new ContextMenuStrip();

            statusItem = Info("Kapeyn");
            detailItem = Info("");
            m.Items.Add(statusItem);
            m.Items.Add(detailItem);
            m.Items.Add(new ToolStripSeparator());

            toggleItem = Item("Turn off", delegate { Toggle(); });
            toggleItem.Font = new Font(toggleItem.Font, FontStyle.Bold);
            toggleItem.ShortcutKeyDisplayString = HotkeyText;
            m.Items.Add(toggleItem);

            durationMenu = new ToolStripMenuItem("Keep awake for");
            durationMenu.DropDownItems.Add(Item("Until I turn it off", delegate { TurnOn(null); }));
            durationMenu.DropDownItems.Add(new ToolStripSeparator());
            foreach (int minutes in DurationMinutes)
            {
                int mins = minutes;
                durationMenu.DropDownItems.Add(Item(Format.Minutes(mins), delegate { TurnOn(mins); }));
            }
            m.Items.Add(durationMenu);
            m.Items.Add(new ToolStripSeparator());

            // Automatic
            var autoMenu = new ToolStripMenuItem("Keep awake automatically");
            callItem = Item("While I'm on a call (microphone in use)", delegate { settings.WhileCall = !settings.WhileCall; Changed(); });
            netItem = Item("While downloading or streaming", delegate { settings.WhileDownloading = !settings.WhileDownloading; Changed(); });
            appsMenu = new ToolStripMenuItem("While these apps are open");
            appsMenu.DropDownItems.Add(Info("..."));
            appsMenu.DropDownOpening += delegate { PopulateApps(); };
            autoMenu.DropDownItems.Add(callItem);
            autoMenu.DropDownItems.Add(netItem);
            autoMenu.DropDownItems.Add(appsMenu);
            autoMenu.DropDownItems.Add(new ToolStripSeparator());

            scheduleItem = Item("During work hours", delegate { settings.ScheduleEnabled = !settings.ScheduleEnabled; Changed(); });
            hoursMenu = new ToolStripMenuItem("Work hours");
            daysMenu = new ToolStripMenuItem("Days");
            for (int i = 1; i <= 7; i++)
            {
                int day = i % 7; // Monday first
                var d = Item(DayNames[day], delegate { settings.ScheduleDays ^= 1 << day; Changed(); });
                d.Tag = day;
                daysMenu.DropDownItems.Add(d);
            }
            startMenu = new ToolStripMenuItem("Start");
            for (int h = 5; h <= 12; h++)
            {
                int hour = h;
                var it = Item(Format.Hour(hour), delegate
                {
                    settings.ScheduleStart = hour;
                    if (settings.ScheduleEnd <= hour) settings.ScheduleEnd = Math.Min(24, hour + 8);
                    Changed();
                });
                it.Tag = hour;
                startMenu.DropDownItems.Add(it);
            }
            endMenu = new ToolStripMenuItem("End");
            for (int h = 13; h <= 23; h++)
            {
                int hour = h;
                var it = Item(Format.Hour(hour), delegate { settings.ScheduleEnd = hour; Changed(); });
                it.Tag = hour;
                endMenu.DropDownItems.Add(it);
            }
            hoursMenu.DropDownItems.Add(daysMenu);
            hoursMenu.DropDownItems.Add(startMenu);
            hoursMenu.DropDownItems.Add(endMenu);
            endOffItem = Item("Turn Kapeyn off when work hours end", delegate { settings.ScheduleEndTurnsOff = !settings.ScheduleEndTurnsOff; Changed(); });
            autoMenu.DropDownItems.Add(scheduleItem);
            autoMenu.DropDownItems.Add(hoursMenu);
            autoMenu.DropDownItems.Add(endOffItem);
            m.Items.Add(autoMenu);

            // Settings
            var settingsMenu = new ToolStripMenuItem("Settings");
            screenItem = Item("Keep screen on", delegate { settings.KeepScreenOn = !settings.KeepScreenOn; Changed(); });
            activityItem = Item("Prevent screen lock && Away status", delegate { settings.SimulateActivity = !settings.SimulateActivity; Changed(); });
            activityItem.ToolTipText = "Sends a tiny invisible input when you're idle, so lock timers and chat apps see you as active.";

            methodMenu = new ToolStripMenuItem("Activity method");
            AddMethod("Mouse wiggle (invisible)", NudgeMethod.Mouse);
            AddMethod("F15 key press", NudgeMethod.Keyboard);
            AddMethod("Both (most robust)", NudgeMethod.Both);

            intervalMenu = new ToolStripMenuItem("Nudge when idle for");
            var autoItem = Item("Auto (recommended)", delegate { settings.NudgeSeconds = 0; Changed(); });
            autoItem.Tag = 0;
            intervalMenu.DropDownItems.Add(autoItem);
            intervalMenu.DropDownItems.Add(new ToolStripSeparator());
            foreach (int seconds in NudgeChoices)
            {
                int secs = seconds;
                var it = Item(Format.Duration(secs), delegate { settings.NudgeSeconds = secs; Changed(); });
                it.Tag = secs;
                intervalMenu.DropDownItems.Add(it);
            }

            lockedItem = Item("Let the screen turn off when I lock the PC", delegate { settings.ScreenOffWhenLocked = !settings.ScreenOffWhenLocked; Changed(); });
            batteryMenu = new ToolStripMenuItem("Pause on low battery");
            var never = Item("Never", delegate { settings.PauseBatteryPercent = 0; Changed(); });
            never.Tag = 0;
            batteryMenu.DropDownItems.Add(never);
            foreach (int p in BatteryChoices)
            {
                int pct = p;
                var it = Item("Below " + pct + "%", delegate { settings.PauseBatteryPercent = pct; Changed(); });
                it.Tag = pct;
                batteryMenu.DropDownItems.Add(it);
            }

            hotkeyItem = Item("Shortcut " + HotkeyText + " turns Kapeyn on/off", delegate
            {
                settings.Hotkey = !settings.Hotkey;
                settings.Save();
                ApplyHotkey();
            });
            startActiveItem = Item("Turn on when Kapeyn starts", delegate { settings.StartActive = !settings.StartActive; settings.Save(); });
            startupItem = Item("Start with Windows", delegate
            {
                try { Startup.Set(!Startup.IsEnabled()); }
                catch (Exception ex) { Notify("Kapeyn", "Couldn't change the startup setting: " + ex.Message); }
            });

            settingsMenu.DropDownItems.Add(screenItem);
            settingsMenu.DropDownItems.Add(activityItem);
            settingsMenu.DropDownItems.Add(methodMenu);
            settingsMenu.DropDownItems.Add(intervalMenu);
            settingsMenu.DropDownItems.Add(new ToolStripSeparator());
            settingsMenu.DropDownItems.Add(lockedItem);
            settingsMenu.DropDownItems.Add(batteryMenu);
            settingsMenu.DropDownItems.Add(new ToolStripSeparator());
            settingsMenu.DropDownItems.Add(hotkeyItem);
            settingsMenu.DropDownItems.Add(startActiveItem);
            settingsMenu.DropDownItems.Add(startupItem);
            m.Items.Add(settingsMenu);
            m.Items.Add(new ToolStripSeparator());

            m.Items.Add(Item("Lock doctor: what locks my PC?", delegate { ShowDoctor(); }));
            m.Items.Add(Item("Command line...", delegate { ShowCliHelp(); }));
            m.Items.Add(new ToolStripSeparator());
            m.Items.Add(Item("Exit", delegate { Exit(); }));
            return m;
        }

        void AddMethod(string label, NudgeMethod method)
        {
            var item = Item(label, delegate { settings.Method = method; Changed(); });
            item.Tag = method;
            methodMenu.DropDownItems.Add(item);
        }

        void PopulateApps()
        {
            appsMenu.DropDownItems.Clear();
            if (settings.WatchApps.Count == 0)
                appsMenu.DropDownItems.Add(Info("None yet - add one below"));
            foreach (var app in settings.WatchApps.ToList())
            {
                var a = app;
                var it = Item(a.Label, delegate { settings.WatchApps.Remove(a); Changed(); });
                it.Checked = true;
                it.ToolTipText = "Click to stop watching " + a.Label;
                appsMenu.DropDownItems.Add(it);
            }
            appsMenu.DropDownItems.Add(new ToolStripSeparator());

            var add = new ToolStripMenuItem("Add an open app");
            var watched = new HashSet<string>(settings.WatchApps.Select(w => w.Name), StringComparer.OrdinalIgnoreCase);
            foreach (var app in Triggers.RunningWindowApps().Where(r => !watched.Contains(r.Name)))
            {
                var a = app;
                add.DropDownItems.Add(Item(a.Label, delegate { settings.WatchApps.Add(a); Changed(); }));
            }
            if (add.DropDownItems.Count == 0) add.DropDownItems.Add(Info("No other open apps"));
            appsMenu.DropDownItems.Add(add);
        }

        void RefreshMenu()
        {
            Evaluate(false);
            statusItem.Text = "Kapeyn: " + StatusLine();
            string detail = DetailLine();
            detailItem.Text = detail ?? "";
            detailItem.Visible = detail != null;
            toggleItem.Text = awake ? "Turn off" : "Turn on";
            toggleItem.ShortcutKeyDisplayString = settings.Hotkey && hotkeyOk ? HotkeyText : "";

            callItem.Checked = settings.WhileCall;
            netItem.Checked = settings.WhileDownloading;
            appsMenu.Text = settings.WatchApps.Count == 0
                ? "While these apps are open"
                : "While these apps are open (" + settings.WatchApps.Count + ")";
            scheduleItem.Checked = settings.ScheduleEnabled;
            scheduleItem.Text = "During work hours (" + Format.Days(settings.ScheduleDays) + ", " +
                                Format.Hour(settings.ScheduleStart) + " - " + Format.Hour(settings.ScheduleEnd) + ")";
            foreach (ToolStripMenuItem d in daysMenu.DropDownItems) d.Checked = (settings.ScheduleDays & (1 << (int)d.Tag)) != 0;
            foreach (ToolStripMenuItem s in startMenu.DropDownItems) s.Checked = (int)s.Tag == settings.ScheduleStart;
            foreach (ToolStripMenuItem e in endMenu.DropDownItems) e.Checked = (int)e.Tag == settings.ScheduleEnd;
            endOffItem.Checked = settings.ScheduleEndTurnsOff;

            screenItem.Checked = settings.KeepScreenOn;
            activityItem.Checked = settings.SimulateActivity;
            methodMenu.Enabled = settings.SimulateActivity;
            intervalMenu.Enabled = settings.SimulateActivity;
            foreach (ToolStripItem i in methodMenu.DropDownItems)
            {
                var mi = i as ToolStripMenuItem;
                if (mi != null) mi.Checked = (NudgeMethod)mi.Tag == settings.Method;
            }
            foreach (ToolStripItem i in intervalMenu.DropDownItems)
            {
                var mi = i as ToolStripMenuItem;
                if (mi == null) continue;
                mi.Checked = (int)mi.Tag == settings.NudgeSeconds;
                if ((int)mi.Tag == 0) mi.Text = "Auto (recommended: " + Format.Duration(autoNudgeSeconds) + ")";
            }
            lockedItem.Checked = settings.ScreenOffWhenLocked;
            foreach (ToolStripItem i in batteryMenu.DropDownItems)
            {
                var mi = i as ToolStripMenuItem;
                if (mi != null) mi.Checked = (int)mi.Tag == settings.PauseBatteryPercent;
            }
            hotkeyItem.Checked = settings.Hotkey;
            hotkeyItem.Text = "Shortcut " + HotkeyText + " turns Kapeyn on/off" +
                              (settings.Hotkey && !hotkeyOk ? " (taken by another app)" : "");
            startActiveItem.Checked = settings.StartActive;
            bool startup = false;
            try { startup = Startup.IsEnabled(); } catch { }
            startupItem.Checked = startup;
        }

        void ShowDoctor()
        {
            Evaluate(false);
            string report = Doctor.Report(settings, awake, StatusLine(), EffectiveNudgeSeconds, settings.NudgeSeconds == 0, true);
            using (var f = new ReportForm("Kapeyn Lock Doctor", report)) f.ShowDialog();
        }

        void ShowCliHelp()
        {
            string dir = Path.GetDirectoryName(Application.ExecutablePath);
            bool installed = File.Exists(Path.Combine(dir, "kapeyn.com"));
            string text =
                "Control Kapeyn from PowerShell or Command Prompt:\n\n" + Cli.HelpText + "\n" +
                (installed
                    ? "The kapeyn command is installed next to Kapeyn.exe (" + dir + ").\n" +
                      "If \"kapeyn\" isn't found, open a new terminal window, or run install.ps1 to add it to your PATH."
                    : "To get the kapeyn command, download Kapeyn-Windows.zip from the Releases page and run install.ps1.");
            using (var f = new ReportForm("Kapeyn command line", text)) f.ShowDialog();
        }
    }

    // Receives the global hotkey (Ctrl+Alt+K).
    sealed class HotkeyWindow : NativeWindow, IDisposable
    {
        const int WM_HOTKEY = 0x0312;
        const int HotkeyId = 0x4B50;
        bool registered;

        public event EventHandler Pressed;

        public HotkeyWindow()
        {
            CreateHandle(new CreateParams());
        }

        public bool Register()
        {
            if (registered) return true;
            registered = Native.RegisterHotKey(Handle, HotkeyId,
                Native.MOD_CONTROL | Native.MOD_ALT | Native.MOD_NOREPEAT, (uint)Keys.K);
            return registered;
        }

        public void Unregister()
        {
            if (!registered) return;
            Native.UnregisterHotKey(Handle, HotkeyId);
            registered = false;
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_HOTKEY && m.WParam.ToInt32() == HotkeyId)
            {
                var h = Pressed;
                if (h != null) h(this, EventArgs.Empty);
            }
            base.WndProc(ref m);
        }

        public void Dispose()
        {
            Unregister();
            if (Handle != IntPtr.Zero) DestroyHandle();
        }
    }
}
