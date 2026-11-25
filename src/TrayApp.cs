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
    sealed class TrayApp : ApplicationContext
    {
        static readonly int[] DurationMinutes = { 15, 30, 60, 120, 240, 480 };
        static readonly int[] NudgeChoices = { 30, 60, 120, 240 };
        static readonly int[] BatteryChoices = { 10, 20, 30 };
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
        readonly Engine engine;                                                 // decides awake/off and why
        readonly Dictionary<int, string> holds = new Dictionary<int, string>(); // kapeyn run: pid -> label
        bool locked, hotkeyOk;
        bool keepMenuOpen, shutdownPending;
        DateTime lastNudge = DateTime.MinValue;

        bool awake { get { return engine.Awake; } }
        bool manualOn { get { return engine.ManualOn; } }
        DateTime? manualUntil { get { return engine.ManualUntil; } }
        int? manualMinutes { get { return engine.ManualMinutes; } }
        readonly HashSet<ToolStripItem> stickyItems = new HashSet<ToolStripItem>(); // clicking these keeps the menu open
        int autoNudgeSeconds = 120;
        DateTime lastPolicyScan = DateTime.MinValue;

        // ---- menu items ----------------------------------------------------------------------
        ToolStripMenuItem statusItem, detailItem, toggleItem, durationMenu;
        ToolStripMenuItem untilMenu, untilDownloadsItem, untilAppMenu, thenMenu;
        ToolStripMenuItem callItem, netItem, appsMenu, scheduleItem;
        ToolStripMenuItem screenItem, activityItem, methodMenu, intervalMenu, lockedItem, batteryMenu,
                          hotkeyItem, startActiveItem, startupItem;

        public TrayApp(EventWaitHandle secondLaunchSignal, bool cliLaunch)
        {
            settings = Settings.Load();
            engine = new Engine("PC", Triggers.InWorkHours(settings, DateTime.Now));

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

            if (settings.StartActive && !cliLaunch) engine.TurnOn(null, DateTime.Now);
            Evaluate(false);

            if (settings.FirstRun)
            {
                settings.Save();
                if (!cliLaunch)
                    Notify("Kapeyn is running",
                        "Your PC will stay awake. Click the cup in the tray to turn it on/off, or press " + HotkeyText +
                        ". Right-click it for options and Help.");
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

            // Gather what's happening; the engine decides what it means.
            var auto = new List<Reason>();
            if (settings.WhileCall)
            {
                string who;
                if (Triggers.MicInUse(out who))
                    auto.Add(new Reason("call", who == null ? "on a call" : "on a call (" + who + ")"));
            }
            // Sample the network once per check (sampling twice in a row would skew the reading).
            bool downloading = (settings.WhileDownloading || engine.Until == UntilKind.Downloads) && net.Update();
            if (settings.WhileDownloading && downloading) auto.Add(new Reason("net", "downloading"));
            foreach (var app in settings.WatchApps)
                if (Triggers.IsRunning(app.Name))
                    auto.Add(new Reason("app:" + app.Name.ToLowerInvariant(), app.Label + " is open"));
            foreach (int pid in holds.Keys.ToList())
            {
                if (!Triggers.IsAlive(pid)) holds.Remove(pid);
                else auto.Add(new Reason("cli:" + pid, "running " + holds[pid]));
            }
            int pct;
            bool onBattery = Triggers.OnBattery(out pct);

            // "Keep awake until": is the thing we're waiting for still going?
            bool untilActive = true;
            switch (engine.Until)
            {
                case UntilKind.Downloads: untilActive = downloading; break;
                case UntilKind.AppCloses: untilActive = Triggers.IsRunning(engine.UntilTarget); break;
                case UntilKind.CommandEnds:
                    int cmdPid;
                    untilActive = int.TryParse(engine.UntilTarget, out cmdPid) && Triggers.IsAlive(cmdPid);
                    break;
            }

            StepEvents ev = engine.Step(settings, now, Triggers.InWorkHours(settings, now), auto, onBattery, pct, untilActive);

            if (ev.TimerEnded || ev.UntilEnded) OnDone(ev);
            if (ev.WorkHoursEnded) OnWorkHoursEnded(ev);
            if (ev.PauseStarted)
                Notify("Kapeyn paused", "Battery is at " + pct + "%, so your PC may sleep. Click the cup to keep it awake anyway.");

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

        /// A timer or "Keep awake until" session finished by itself: do what "When it's done" says.
        void OnDone(StepEvents ev)
        {
            switch (ev.Then)
            {
                case DoneAction.Lock:
                    Notify("Kapeyn", ev.Finished + " Kapeyn locked your PC.");
                    if (!locked) Native.LockWorkStation();
                    break;
                case DoneAction.Sleep:
                case DoneAction.ShutDown:
                    DoneAction action = ev.Then;
                    string heading = ev.Finished;
                    invoker.BeginInvoke(new Action(delegate { Countdown(heading, action); }));
                    break;
                default:
                    Notify("Kapeyn", ev.Finished + " Your PC can sleep again.");
                    break;
            }
        }

        void OnWorkHoursEnded(StepEvents ev)
        {
            if (ev.Shutdown)
            {
                invoker.BeginInvoke(new Action(delegate { Countdown("Work hours are over.", DoneAction.ShutDown); }));
            }
            else if (ev.Lock)
            {
                Notify("Kapeyn", "Work hours are over. Kapeyn turned itself off and locked your PC.");
                if (!locked) Native.LockWorkStation();
            }
            else if (ev.WasManualOn)
            {
                Notify("Kapeyn", "Work hours are over, so Kapeyn turned itself off.");
            }
        }

        /// 60-second countdown with Cancel, then sleep or shut down.
        void Countdown(string heading, DoneAction action)
        {
            if (shutdownPending) return;
            shutdownPending = true;
            bool sleep = action == DoneAction.Sleep;
            try
            {
                DialogResult result;
                using (var f = new CountdownForm(60, heading,
                           sleep ? "put this PC to sleep" : "shut down this PC",
                           sleep ? "Sleep now" : "Shut down now"))
                    result = f.ShowDialog();
                if (result != DialogResult.OK)
                {
                    Notify("Kapeyn", (sleep ? "Sleep" : "Shutdown") + " cancelled. Kapeyn is off; your PC may sleep.");
                }
                else if (sleep)
                {
                    Native.Sleep();
                }
                else
                {
                    // Normal (not forced) shutdown: apps with unsaved work can still stop it.
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("shutdown.exe", "/s /t 0")
                    {
                        UseShellExecute = false,
                        CreateNoWindow = true,
                    });
                }
            }
            catch (Exception ex)
            {
                Notify("Kapeyn", "Couldn't " + (sleep ? "sleep" : "shut down") + ": " + ex.Message);
            }
            finally { shutdownPending = false; }
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
            engine.TurnOn(minutes, DateTime.Now);
            lastNudge = DateTime.MinValue;
            Evaluate(false);
        }

        void TurnOnUntil(UntilKind kind, string target, string label)
        {
            engine.TurnOnUntil(kind, target, label, DateTime.Now);
            lastNudge = DateTime.MinValue;
            Evaluate(false);
        }

        void TurnOff()
        {
            engine.TurnOff();
            Evaluate(false);
        }

        void SetThen(DoneAction action)
        {
            engine.Then = action;
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
            if (e.Mode == PowerModes.Resume)
            {
                // Woke from sleep: start fresh, so an end of work hours that passed while asleep isn't acted on.
                invoker.BeginInvoke(new Action(delegate
                {
                    engine.Rebaseline(Triggers.InWorkHours(settings, DateTime.Now));
                    Evaluate(false);
                }));
            }
            else if (e.Mode == PowerModes.StatusChange)
            {
                invoker.BeginInvoke(new Action(delegate { Evaluate(false); }));
            }
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
                        engine.Unsnooze("cli:" + n);
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
                case "then":
                    DoneAction action;
                    if (parts.Length < 2 || !Engine.TryParseThen(string.Join(" ", parts.Skip(1).ToArray()), out action))
                        return "error: expected then sleep | lock | shutdown | nothing";
                    SetThen(action);
                    return "Kapeyn: " + StatusLine();
                case "until":
                    return HandleUntil(parts.Length > 1 ? parts[1].ToLowerInvariant() : "",
                                       parts.Length > 2 ? parts[2].Trim() : "");
                case "doctor":
                    return Doctor.Report(settings, awake, StatusLine(), EffectiveNudgeSeconds, settings.NudgeSeconds == 0, true);
                case "guide":
                    invoker.BeginInvoke(new Action(delegate { ShowHelp(null); }));
                    return "Opened the Kapeyn guide.";
                default:
                    return "error: unknown command \"" + verb + "\"";
            }
        }

        /// until downloads | until app <name> | until pid <pid> <label>
        string HandleUntil(string what, string rest)
        {
            switch (what)
            {
                case "downloads":
                    TurnOnUntil(UntilKind.Downloads, null, null);
                    return "Kapeyn: " + StatusLine();
                case "app":
                    WatchedApp app = FindOpenApp(rest);
                    if (app == null) return "error: no open app called \"" + rest + "\". Check the name in Task Manager.";
                    TurnOnUntil(UntilKind.AppCloses, app.Name, app.Label);
                    return "Kapeyn: " + StatusLine();
                case "pid":
                    string[] p = rest.Split(new[] { ' ' }, 2, StringSplitOptions.RemoveEmptyEntries);
                    int pid;
                    if (p.Length == 0 || !int.TryParse(p[0], out pid)) return "error: expected until pid <pid> <label>";
                    TurnOnUntil(UntilKind.CommandEnds, pid.ToString(), p.Length > 1 ? p[1] : "the command");
                    return "Kapeyn: " + StatusLine();
                default:
                    return "error: expected until downloads | until app <name>";
            }
        }

        /// An open app by its process name ("chrome", "chrome.exe") or the name Kapeyn shows ("Google Chrome").
        static WatchedApp FindOpenApp(string name)
        {
            string n = (name ?? "").Trim().Trim('"');
            if (n.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) n = n.Substring(0, n.Length - 4);
            if (n.Length == 0) return null;
            var open = Triggers.RunningWindowApps();
            var match = open.FirstOrDefault(a => string.Equals(a.Name, n, StringComparison.OrdinalIgnoreCase))
                     ?? open.FirstOrDefault(a => string.Equals(a.Label, n, StringComparison.OrdinalIgnoreCase));
            if (match != null) return match;
            // Background programs without a window (a download tool, a render job) by process name.
            return Triggers.IsRunning(n) ? new WatchedApp { Name = n, Label = n } : null;
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

        string StatusLine() { return engine.StatusLine(); }

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
                          " | work hours " + (settings.ScheduleEnabled ? Format.Schedule(settings) : "no") +
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
            var untilOff = Item("Until I turn it off", delegate { TurnOn(null); });
            untilOff.Tag = 0;
            durationMenu.DropDownItems.Add(untilOff);
            durationMenu.DropDownItems.Add(new ToolStripSeparator());
            foreach (int minutes in DurationMinutes)
            {
                int mins = minutes;
                var it = Item(Format.Minutes(mins), delegate { TurnOn(mins); });
                it.Tag = mins;
                durationMenu.DropDownItems.Add(it);
            }
            m.Items.Add(durationMenu);

            // Keep awake until something finishes
            untilMenu = new ToolStripMenuItem("Keep awake until");
            untilDownloadsItem = Item("Downloads finish", delegate { TurnOnUntil(UntilKind.Downloads, null, null); });
            untilDownloadsItem.ToolTipText = "Stays awake while files download, and finishes after " +
                                             Engine.DownloadQuietMinutes + " quiet minutes.";
            untilAppMenu = new ToolStripMenuItem("An app closes");
            untilAppMenu.DropDownItems.Add(Info("..."));
            untilAppMenu.DropDownOpening += delegate { PopulateUntilApps(); };
            untilMenu.DropDownItems.Add(untilDownloadsItem);
            untilMenu.DropDownItems.Add(untilAppMenu);
            m.Items.Add(untilMenu);

            // What to do when a timer or "until" finishes
            thenMenu = new ToolStripMenuItem("When it's done");
            foreach (DoneAction a in new[] { DoneAction.Nothing, DoneAction.Sleep, DoneAction.Lock, DoneAction.ShutDown })
            {
                DoneAction action = a;
                var it = Sticky(ThenMenuText(action), delegate { SetThen(action); });
                it.Tag = action;
                thenMenu.DropDownItems.Add(it);
                if (action == DoneAction.Nothing) thenMenu.DropDownItems.Add(new ToolStripSeparator());
            }
            m.Items.Add(thenMenu);
            m.Items.Add(new ToolStripSeparator());

            // Automatic
            var autoMenu = new ToolStripMenuItem("Keep awake automatically");
            callItem = Sticky("While I'm on a call (microphone in use)", delegate { settings.WhileCall = !settings.WhileCall; Changed(); });
            netItem = Sticky("While downloading or streaming", delegate { settings.WhileDownloading = !settings.WhileDownloading; Changed(); });
            appsMenu = new ToolStripMenuItem("While these apps are open");
            appsMenu.DropDownItems.Add(Info("..."));
            appsMenu.DropDownOpening += delegate { PopulateApps(); };
            autoMenu.DropDownItems.Add(callItem);
            autoMenu.DropDownItems.Add(netItem);
            autoMenu.DropDownItems.Add(appsMenu);
            autoMenu.DropDownItems.Add(new ToolStripSeparator());

            scheduleItem = Sticky("During work hours", delegate { settings.ScheduleEnabled = !settings.ScheduleEnabled; Changed(); });
            autoMenu.DropDownItems.Add(scheduleItem);
            autoMenu.DropDownItems.Add(Item("Set work hours...", delegate { ShowWorkHours(); }));
            m.Items.Add(autoMenu);

            // Settings
            var settingsMenu = new ToolStripMenuItem("Settings");
            screenItem = Sticky("Keep screen on", delegate { settings.KeepScreenOn = !settings.KeepScreenOn; Changed(); });
            activityItem = Sticky("Prevent screen lock && Away status", delegate { settings.SimulateActivity = !settings.SimulateActivity; Changed(); });
            activityItem.ToolTipText = "Sends a tiny invisible input when you're idle, so lock timers and chat apps see you as active.";

            methodMenu = new ToolStripMenuItem("Activity method");
            AddMethod("Mouse wiggle (invisible)", NudgeMethod.Mouse);
            AddMethod("F15 key press", NudgeMethod.Keyboard);
            AddMethod("Both (most robust)", NudgeMethod.Both);

            intervalMenu = new ToolStripMenuItem("Nudge when idle for");
            var autoItem = Sticky("Auto (recommended)", delegate { settings.NudgeSeconds = 0; Changed(); });
            autoItem.Tag = 0;
            intervalMenu.DropDownItems.Add(autoItem);
            intervalMenu.DropDownItems.Add(new ToolStripSeparator());
            foreach (int seconds in NudgeChoices)
            {
                int secs = seconds;
                var it = Sticky(Format.Duration(secs), delegate { settings.NudgeSeconds = secs; Changed(); });
                it.Tag = secs;
                intervalMenu.DropDownItems.Add(it);
            }

            lockedItem = Sticky("Let the screen turn off when I lock the PC", delegate { settings.ScreenOffWhenLocked = !settings.ScreenOffWhenLocked; Changed(); });
            batteryMenu = new ToolStripMenuItem("Pause on low battery");
            var never = Sticky("Never", delegate { settings.PauseBatteryPercent = 0; Changed(); });
            never.Tag = 0;
            batteryMenu.DropDownItems.Add(never);
            foreach (int p in BatteryChoices)
            {
                int pct = p;
                var it = Sticky("Below " + pct + "%", delegate { settings.PauseBatteryPercent = pct; Changed(); });
                it.Tag = pct;
                batteryMenu.DropDownItems.Add(it);
            }

            hotkeyItem = Sticky("Shortcut " + HotkeyText + " turns Kapeyn on/off", delegate
            {
                settings.Hotkey = !settings.Hotkey;
                settings.Save();
                ApplyHotkey();
            });
            startActiveItem = Sticky("Turn on when Kapeyn starts", delegate { settings.StartActive = !settings.StartActive; settings.Save(); });
            startupItem = Sticky("Start with Windows", delegate
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
            m.Items.Add(Item("Command line...", delegate { ShowHelp("Command line"); }));
            m.Items.Add(Item("Help", delegate { ShowHelp(null); }));
            m.Items.Add(new ToolStripSeparator());
            m.Items.Add(Item("Exit", delegate { Exit(); }));
            KeepOpenOnStickyClicks(m);
            return m;
        }

        /// A menu item whose click changes a setting and leaves the menu open, so you can change several in a row.
        ToolStripMenuItem Sticky(string text, Action change)
        {
            var it = new ToolStripMenuItem(text, null, delegate { change(); RefreshMenu(); });
            stickyItems.Add(it);
            return it;
        }

        // Clicking an item closes every open menu level with reason ItemClicked. For sticky items, cancel that
        // on each level (root and submenus) so the whole menu stays where it is.
        void KeepOpenOnStickyClicks(ToolStripDropDown dropDown)
        {
            dropDown.ItemClicked += delegate(object s, ToolStripItemClickedEventArgs e)
            {
                if (!stickyItems.Contains(e.ClickedItem)) return;
                keepMenuOpen = true;
                invoker.BeginInvoke(new Action(delegate { keepMenuOpen = false; }));
            };
            dropDown.Closing += delegate(object s, ToolStripDropDownClosingEventArgs e)
            {
                if (e.CloseReason == ToolStripDropDownCloseReason.ItemClicked && keepMenuOpen) e.Cancel = true;
            };
            foreach (ToolStripItem i in dropDown.Items)
            {
                var mi = i as ToolStripMenuItem;
                if (mi != null && mi.HasDropDownItems) KeepOpenOnStickyClicks(mi.DropDown);
            }
        }

        static string ThenMenuText(DoneAction a)
        {
            switch (a)
            {
                case DoneAction.Sleep: return "Sleep";
                case DoneAction.Lock: return "Lock the PC";
                case DoneAction.ShutDown: return "Shut down";
                default: return "Do nothing";
            }
        }

        void PopulateUntilApps()
        {
            untilAppMenu.DropDownItems.Clear();
            foreach (var app in Triggers.RunningWindowApps())
            {
                var a = app;
                var it = Item(a.Label, delegate { TurnOnUntil(UntilKind.AppCloses, a.Name, a.Label); });
                it.Checked = manualOn && engine.Until == UntilKind.AppCloses &&
                             string.Equals(engine.UntilTarget, a.Name, StringComparison.OrdinalIgnoreCase);
                untilAppMenu.DropDownItems.Add(it);
            }
            if (untilAppMenu.DropDownItems.Count == 0) untilAppMenu.DropDownItems.Add(Info("No open apps"));
        }

        void AddMethod(string label, NudgeMethod method)
        {
            var item = Sticky(label, delegate { settings.Method = method; Changed(); });
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

            // Keep awake for: check the current choice; a timed one also shows the time left.
            foreach (ToolStripItem i in durationMenu.DropDownItems)
            {
                var mi = i as ToolStripMenuItem;
                if (mi == null) continue;
                int minutes = (int)mi.Tag;
                bool current = manualOn && (minutes == 0
                    ? !manualUntil.HasValue && engine.Until == UntilKind.None
                    : manualUntil.HasValue && manualMinutes == minutes);
                mi.Checked = current;
                string label = minutes == 0 ? "Until I turn it off" : Format.Minutes(minutes);
                mi.Text = current && minutes > 0
                    ? label + "  (" + Format.Remaining(manualUntil.Value - DateTime.Now) + " left)"
                    : label;
            }
            durationMenu.Text = manualOn && manualUntil.HasValue
                ? "Keep awake for (" + Format.Remaining(manualUntil.Value - DateTime.Now) + " left)"
                : "Keep awake for";

            // Keep awake until: check and name the current choice.
            bool until = manualOn && engine.Until != UntilKind.None;
            untilDownloadsItem.Checked = until && engine.Until == UntilKind.Downloads;
            untilAppMenu.Checked = until && engine.Until == UntilKind.AppCloses;
            untilMenu.Text = until ? "Keep awake until " + engine.UntilText() : "Keep awake until";

            // When it's done: applies to a timer or an "until"; shown on the menu so it's never a surprise.
            foreach (ToolStripItem i in thenMenu.DropDownItems)
            {
                var mi = i as ToolStripMenuItem;
                if (mi != null) mi.Checked = (DoneAction)mi.Tag == engine.Then;
            }
            thenMenu.Text = engine.Then == DoneAction.Nothing
                ? "When it's done"
                : "When it's done: " + ThenMenuText(engine.Then);

            callItem.Checked = settings.WhileCall;
            netItem.Checked = settings.WhileDownloading;
            appsMenu.Text = settings.WatchApps.Count == 0
                ? "While these apps are open"
                : "While these apps are open (" + settings.WatchApps.Count + ")";
            scheduleItem.Checked = settings.ScheduleEnabled;
            scheduleItem.Text = "During work hours (" + Format.Schedule(settings) + ")";

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

        void ShowWorkHours()
        {
            using (var f = new WorkHoursForm(settings))
            {
                if (f.ShowDialog() != DialogResult.OK) return;
            }
            settings.Save();
            // New times: start from where we are now, so saving never counts as "work hours just ended".
            engine.Rebaseline(Triggers.InWorkHours(settings, DateTime.Now));
            Evaluate(false);
        }

        void ShowDoctor()
        {
            Evaluate(false);
            string report = Doctor.Report(settings, awake, StatusLine(), EffectiveNudgeSeconds, settings.NudgeSeconds == 0, true);
            using (var f = new ReportForm("Kapeyn Lock Doctor", report)) f.ShowDialog();
        }

        HelpForm helpWindow;

        /// Opens the guide (one window; brings it to the front if it's already open).
        void ShowHelp(string topic)
        {
            if (helpWindow == null || helpWindow.IsDisposed)
            {
                helpWindow = new HelpForm(topic ?? "Getting started");
                helpWindow.FormClosed += delegate { helpWindow.Dispose(); helpWindow = null; };
                helpWindow.Show();
            }
            else
            {
                helpWindow.ShowTopic(topic);
                if (helpWindow.WindowState == FormWindowState.Minimized) helpWindow.WindowState = FormWindowState.Normal;
            }
            helpWindow.Activate();
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
