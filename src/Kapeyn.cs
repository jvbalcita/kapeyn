// Kapeyn - keeps your Windows PC awake.
// Single-file WinForms tray app. Compiles with the csc.exe that ships with Windows (.NET Framework 4.x, C# 5).
//
// How it keeps the machine awake (two layers, both on by default):
//   1. Power request  - SetThreadExecutionState tells Windows not to sleep / turn the display off.
//   2. Activity nudge - when you have been idle for a while, Kapeyn sends a tiny invisible input
//                       (1px mouse wiggle that is put back, and/or an F15 key press). This resets the
//                       idle timer that drives screen-lock policies and "Away" status in Teams/Slack,
//                       which a power request alone cannot prevent.

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

[assembly: AssemblyTitle("Kapeyn")]
[assembly: AssemblyProduct("Kapeyn")]
[assembly: AssemblyDescription("Keeps your PC awake")]
[assembly: AssemblyVersion("1.0.0.0")]
[assembly: AssemblyFileVersion("1.0.0.0")]

namespace Kapeyn
{
    static class Program
    {
        const string MutexName = "Local\\Kapeyn.SingleInstance";
        const string SignalName = "Local\\Kapeyn.SecondLaunch";

        [STAThread]
        static int Main(string[] args)
        {
            // Build helper: write the app icon to disk (used by build.ps1).
            if (args.Length == 2 && args[0] == "--export-icon")
            {
                IconArt.SaveIco(args[1]);
                return 0;
            }

            // Diagnostic: send one nudge of each kind; exit code = number of input events Windows accepted (expect 4).
            if (args.Length == 1 && args[0] == "--nudge-test")
                return (int)Native.Nudge(NudgeMethod.Both);

            bool createdNew;
            using (var mutex = new Mutex(true, MutexName, out createdNew))
            {
                if (!createdNew)
                {
                    // Already running: poke the running copy (it turns itself on and says hello) and quit.
                    try
                    {
                        using (var signal = EventWaitHandle.OpenExisting(SignalName)) signal.Set();
                    }
                    catch { }
                    return 0;
                }

                using (var signal = new EventWaitHandle(false, EventResetMode.AutoReset, SignalName))
                {
                    Application.EnableVisualStyles();
                    Application.SetCompatibleTextRenderingDefault(false);
                    Application.Run(new TrayApp(signal));
                }
                GC.KeepAlive(mutex);
            }
            return 0;
        }
    }

    // ------------------------------------------------------------------------------------------
    // Tray application
    // ------------------------------------------------------------------------------------------
    sealed class TrayApp : ApplicationContext
    {
        static readonly int[] DurationMinutes = { 15, 30, 60, 120, 240, 480 };
        static readonly int[] NudgeChoices = { 30, 60, 120, 240 };

        readonly Settings settings;
        readonly Control invoker;
        readonly NotifyIcon tray;
        readonly ContextMenuStrip menu;
        readonly System.Windows.Forms.Timer tick;
        readonly RegisteredWaitHandle secondLaunchWait;
        readonly Icon iconOn;
        readonly Icon iconOff;

        bool active;
        DateTime? until;

        ToolStripMenuItem statusItem, toggleItem, durationMenu, screenItem, activityItem,
                          methodMenu, intervalMenu, startActiveItem, startupItem;

        public TrayApp(EventWaitHandle secondLaunchSignal)
        {
            settings = Settings.Load();

            invoker = new Control();
            IntPtr forceHandle = invoker.Handle; // create the window handle so BeginInvoke works
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

            tick = new System.Windows.Forms.Timer();
            tick.Interval = 5000;
            tick.Tick += delegate { OnTick(); };
            tick.Start();

            secondLaunchWait = ThreadPool.RegisterWaitForSingleObject(secondLaunchSignal,
                delegate { invoker.BeginInvoke(new Action(OnSecondLaunch)); }, null, -1, false);

            SystemEvents.PowerModeChanged += OnPowerModeChanged;

            Startup.RepairPath();

            if (settings.StartActive) Activate(null);
            else UpdateUi();

            if (settings.FirstRun)
            {
                settings.Save();
                tray.ShowBalloonTip(5000, "Kapeyn is running",
                    "Your PC will stay awake. Click the cup in the system tray to turn it on/off; right-click for options.",
                    ToolTipIcon.Info);
            }
        }

        // ---- state -------------------------------------------------------------------------

        void Activate(TimeSpan? duration)
        {
            active = true;
            until = duration.HasValue ? DateTime.Now + duration.Value : (DateTime?)null;
            ApplyPowerRequest();
            UpdateUi();
        }

        void Deactivate(string message)
        {
            active = false;
            until = null;
            Native.SetThreadExecutionState(Native.ES_CONTINUOUS); // clear our power request
            UpdateUi();
            if (message != null) tray.ShowBalloonTip(4000, "Kapeyn", message, ToolTipIcon.Info);
        }

        void Toggle()
        {
            if (active) Deactivate(null);
            else Activate(null);
        }

        void ApplyPowerRequest()
        {
            uint flags = Native.ES_CONTINUOUS | Native.ES_SYSTEM_REQUIRED;
            if (settings.KeepScreenOn) flags |= Native.ES_DISPLAY_REQUIRED;
            Native.SetThreadExecutionState(flags);
        }

        void OnTick()
        {
            if (!active) return;

            if (until.HasValue && DateTime.Now >= until.Value)
            {
                Deactivate("Timer finished. Your PC can sleep again.");
                return;
            }

            ApplyPowerRequest(); // cheap; re-asserting guards against anything that cleared it

            if (settings.SimulateActivity && Native.GetIdleTime().TotalSeconds >= settings.NudgeSeconds - 2)
                Native.Nudge(settings.Method);

            UpdateUi();
        }

        void OnPowerModeChanged(object sender, PowerModeChangedEventArgs e)
        {
            if (e.Mode == PowerModes.Resume && active)
                invoker.BeginInvoke(new Action(ApplyPowerRequest));
        }

        void OnSecondLaunch()
        {
            if (!active) Activate(null);
            tray.ShowBalloonTip(4000, "Kapeyn is already running",
                "Your PC is being kept awake. Find the cup icon in the system tray (click ^ on the taskbar if it's hidden).",
                ToolTipIcon.Info);
        }

        void Exit()
        {
            tick.Stop();
            secondLaunchWait.Unregister(null);
            SystemEvents.PowerModeChanged -= OnPowerModeChanged;
            Native.SetThreadExecutionState(Native.ES_CONTINUOUS);
            tray.Visible = false;
            tray.Dispose();
            menu.Dispose();
            invoker.Dispose();
            ExitThread();
        }

        // ---- UI ----------------------------------------------------------------------------

        string StatusText()
        {
            if (!active) return "Off - PC may sleep";
            if (!until.HasValue) return "Awake - indefinitely";
            return "Awake - " + FormatRemaining(until.Value - DateTime.Now) + " left";
        }

        static string FormatRemaining(TimeSpan r)
        {
            int totalMin = Math.Max(1, (int)Math.Ceiling(r.TotalMinutes));
            int h = totalMin / 60, m = totalMin % 60;
            if (h == 0) return totalMin + " min";
            return m == 0 ? h + " h" : h + " h " + m + " min";
        }

        static string FormatMinutes(int minutes)
        {
            if (minutes < 60) return minutes + " minutes";
            int h = minutes / 60;
            return h == 1 ? "1 hour" : h + " hours";
        }

        static string FormatSeconds(int seconds)
        {
            if (seconds < 60) return seconds + " seconds";
            int m = seconds / 60;
            return m == 1 ? "1 minute" : m + " minutes";
        }

        void UpdateUi()
        {
            tray.Icon = active ? iconOn : iconOff;
            string text = "Kapeyn: " + StatusText();
            tray.Text = text.Length > 63 ? text.Substring(0, 63) : text;
        }

        ContextMenuStrip BuildMenu()
        {
            var m = new ContextMenuStrip();

            statusItem = new ToolStripMenuItem("Kapeyn");
            statusItem.Enabled = false;
            m.Items.Add(statusItem);
            m.Items.Add(new ToolStripSeparator());

            toggleItem = new ToolStripMenuItem("Turn off", null, delegate { Toggle(); });
            toggleItem.Font = new Font(toggleItem.Font, FontStyle.Bold);
            m.Items.Add(toggleItem);

            durationMenu = new ToolStripMenuItem("Keep awake for");
            durationMenu.DropDownItems.Add(new ToolStripMenuItem("Indefinitely", null, delegate { Activate(null); }));
            durationMenu.DropDownItems.Add(new ToolStripSeparator());
            foreach (int minutes in DurationMinutes)
            {
                int mins = minutes;
                durationMenu.DropDownItems.Add(new ToolStripMenuItem(FormatMinutes(mins), null,
                    delegate { Activate(TimeSpan.FromMinutes(mins)); }));
            }
            m.Items.Add(durationMenu);
            m.Items.Add(new ToolStripSeparator());

            screenItem = new ToolStripMenuItem("Keep screen on", null, delegate
            {
                settings.KeepScreenOn = !settings.KeepScreenOn;
                settings.Save();
                if (active) ApplyPowerRequest();
            });
            m.Items.Add(screenItem);

            activityItem = new ToolStripMenuItem("Prevent screen lock && Away status", null, delegate
            {
                settings.SimulateActivity = !settings.SimulateActivity;
                settings.Save();
            });
            activityItem.ToolTipText = "Sends a tiny invisible input when you're idle, so lock timers and chat apps see you as active.";
            m.Items.Add(activityItem);

            methodMenu = new ToolStripMenuItem("Activity method");
            AddMethod(methodMenu, "Mouse wiggle (invisible)", NudgeMethod.Mouse);
            AddMethod(methodMenu, "F15 key press", NudgeMethod.Keyboard);
            AddMethod(methodMenu, "Both (most robust)", NudgeMethod.Both);
            m.Items.Add(methodMenu);

            intervalMenu = new ToolStripMenuItem("Nudge when idle for");
            foreach (int seconds in NudgeChoices)
            {
                int secs = seconds;
                var item = new ToolStripMenuItem(FormatSeconds(secs), null, delegate
                {
                    settings.NudgeSeconds = secs;
                    settings.Save();
                });
                item.Tag = secs;
                intervalMenu.DropDownItems.Add(item);
            }
            m.Items.Add(intervalMenu);
            m.Items.Add(new ToolStripSeparator());

            startActiveItem = new ToolStripMenuItem("Turn on when Kapeyn starts", null, delegate
            {
                settings.StartActive = !settings.StartActive;
                settings.Save();
            });
            m.Items.Add(startActiveItem);

            startupItem = new ToolStripMenuItem("Start with Windows", null, delegate
            {
                try { Startup.Set(!Startup.IsEnabled()); }
                catch (Exception ex)
                {
                    tray.ShowBalloonTip(4000, "Kapeyn", "Couldn't change startup setting: " + ex.Message, ToolTipIcon.Warning);
                }
            });
            m.Items.Add(startupItem);
            m.Items.Add(new ToolStripSeparator());

            m.Items.Add(new ToolStripMenuItem("Exit", null, delegate { Exit(); }));
            return m;
        }

        void AddMethod(ToolStripMenuItem parent, string label, NudgeMethod method)
        {
            var item = new ToolStripMenuItem(label, null, delegate
            {
                settings.Method = method;
                settings.Save();
            });
            item.Tag = method;
            parent.DropDownItems.Add(item);
        }

        void RefreshMenu()
        {
            statusItem.Text = "Kapeyn: " + StatusText();
            toggleItem.Text = active ? "Turn off" : "Turn on";

            foreach (ToolStripItem i in durationMenu.DropDownItems)
            {
                var mi = i as ToolStripMenuItem;
                if (mi != null && mi.Text == "Indefinitely") mi.Checked = active && !until.HasValue;
            }

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
                if (mi != null) mi.Checked = (int)mi.Tag == settings.NudgeSeconds;
            }

            startActiveItem.Checked = settings.StartActive;
            bool startup = false;
            try { startup = Startup.IsEnabled(); } catch { }
            startupItem.Checked = startup;
        }
    }

    // ------------------------------------------------------------------------------------------
    // Settings (%APPDATA%\Kapeyn\settings.ini)
    // ------------------------------------------------------------------------------------------
    enum NudgeMethod { Mouse, Keyboard, Both }

    sealed class Settings
    {
        public bool StartActive = true;
        public bool KeepScreenOn = true;
        public bool SimulateActivity = true;
        public NudgeMethod Method = NudgeMethod.Mouse;
        public int NudgeSeconds = 60;
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
                        case "startactive": if (bool.TryParse(val, out b)) s.StartActive = b; break;
                        case "keepscreenon": if (bool.TryParse(val, out b)) s.KeepScreenOn = b; break;
                        case "simulateactivity": if (bool.TryParse(val, out b)) s.SimulateActivity = b; break;
                        case "nudgemethod": if (Enum.TryParse(val, true, out nm)) s.Method = nm; break;
                        case "nudgeseconds":
                            if (int.TryParse(val, out n) && n >= 10 && n <= 3600) s.NudgeSeconds = n;
                            break;
                    }
                }
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
                    "StartActive=" + StartActive,
                    "KeepScreenOn=" + KeepScreenOn,
                    "SimulateActivity=" + SimulateActivity,
                    "NudgeMethod=" + Method,
                    "NudgeSeconds=" + NudgeSeconds,
                });
            }
            catch { }
        }
    }

    // ------------------------------------------------------------------------------------------
    // Start with Windows (HKCU\...\Run, no admin needed)
    // ------------------------------------------------------------------------------------------
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

    // ------------------------------------------------------------------------------------------
    // Win32
    // ------------------------------------------------------------------------------------------
    static class Native
    {
        public const uint ES_SYSTEM_REQUIRED = 0x00000001;
        public const uint ES_DISPLAY_REQUIRED = 0x00000002;
        public const uint ES_CONTINUOUS = 0x80000000;

        [DllImport("kernel32.dll")]
        public static extern uint SetThreadExecutionState(uint esFlags);

        [StructLayout(LayoutKind.Sequential)]
        struct LASTINPUTINFO { public uint cbSize; public uint dwTime; }

        [DllImport("user32.dll")]
        static extern bool GetLastInputInfo(ref LASTINPUTINFO plii);

        public static TimeSpan GetIdleTime()
        {
            var lii = new LASTINPUTINFO();
            lii.cbSize = (uint)Marshal.SizeOf(typeof(LASTINPUTINFO));
            if (!GetLastInputInfo(ref lii)) return TimeSpan.Zero;
            uint now = unchecked((uint)Environment.TickCount);
            return TimeSpan.FromMilliseconds(unchecked(now - lii.dwTime));
        }

        const uint INPUT_MOUSE = 0;
        const uint INPUT_KEYBOARD = 1;
        const uint MOUSEEVENTF_MOVE = 0x0001;
        const uint KEYEVENTF_KEYUP = 0x0002;
        const ushort VK_F15 = 0x7E;

        [StructLayout(LayoutKind.Sequential)]
        struct MOUSEINPUT { public int dx; public int dy; public uint mouseData; public uint dwFlags; public uint time; public IntPtr dwExtraInfo; }

        [StructLayout(LayoutKind.Sequential)]
        struct KEYBDINPUT { public ushort wVk; public ushort wScan; public uint dwFlags; public uint time; public IntPtr dwExtraInfo; }

        [StructLayout(LayoutKind.Explicit)]
        struct InputUnion
        {
            [FieldOffset(0)] public MOUSEINPUT mi;
            [FieldOffset(0)] public KEYBDINPUT ki;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct INPUT { public uint type; public InputUnion u; }

        [StructLayout(LayoutKind.Sequential)]
        struct POINT { public int X; public int Y; }

        [DllImport("user32.dll", SetLastError = true)]
        static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

        [DllImport("user32.dll")]
        static extern bool GetCursorPos(out POINT lpPoint);

        [DllImport("user32.dll")]
        static extern bool SetCursorPos(int x, int y);

        static INPUT MouseMove(int dx, int dy)
        {
            var i = new INPUT();
            i.type = INPUT_MOUSE;
            i.u.mi.dx = dx;
            i.u.mi.dy = dy;
            i.u.mi.dwFlags = MOUSEEVENTF_MOVE;
            return i;
        }

        static INPUT Key(ushort vk, bool up)
        {
            var i = new INPUT();
            i.type = INPUT_KEYBOARD;
            i.u.ki.wVk = vk;
            i.u.ki.dwFlags = up ? KEYEVENTF_KEYUP : 0;
            return i;
        }

        public static uint Nudge(NudgeMethod method)
        {
            bool mouse = method == NudgeMethod.Mouse || method == NudgeMethod.Both;
            bool key = method == NudgeMethod.Keyboard || method == NudgeMethod.Both;

            var inputs = new List<INPUT>();
            if (mouse) { inputs.Add(MouseMove(1, 0)); inputs.Add(MouseMove(-1, 0)); }
            if (key) { inputs.Add(Key(VK_F15, false)); inputs.Add(Key(VK_F15, true)); }

            POINT before = new POINT();
            bool havePos = mouse && GetCursorPos(out before);
            uint sent = SendInput((uint)inputs.Count, inputs.ToArray(), Marshal.SizeOf(typeof(INPUT)));
            if (havePos) SetCursorPos(before.X, before.Y); // put the pointer exactly where it was
            return sent;
        }

        [DllImport("user32.dll")]
        public static extern bool DestroyIcon(IntPtr hIcon);
    }

    // ------------------------------------------------------------------------------------------
    // Icon drawing (coffee cup; steaming amber = awake, grey outline = off)
    // ------------------------------------------------------------------------------------------
    static class IconArt
    {
        static readonly Color Amber = Color.FromArgb(255, 222, 128, 44);
        static readonly Color Grey = Color.FromArgb(255, 140, 140, 140);

        public static Bitmap Draw(int size, bool on)
        {
            var bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.Clear(Color.Transparent);

                // Artwork is designed on a 32x32 grid.
                float s = size / 32f;
                g.ScaleTransform(s, s);
                g.TranslateTransform(1.5f, 0f);

                Color c = on ? Amber : Grey;
                float stroke = size <= 16 ? 3.2f : 2.6f; // a bit thicker at tiny sizes so it stays readable

                using (var pen = new Pen(c, stroke))
                using (var brush = new SolidBrush(c))
                {
                    pen.StartCap = LineCap.Round;
                    pen.EndCap = LineCap.Round;
                    pen.LineJoin = LineJoin.Round;

                    // handle (right half only, so it doesn't show inside the hollow "off" cup)
                    g.DrawArc(pen, 17.5f, 14.5f, 8.5f, 8f, -100f, 200f);

                    // cup body: flat top, rounded bottom
                    using (var body = RoundedBottom(3f, 12f, 18f, 15f, 5f))
                    {
                        if (on) g.FillPath(brush, body);
                        else g.DrawPath(pen, body);
                    }

                    // saucer
                    g.DrawLine(pen, 1.5f, 29.5f, 23.5f, 29.5f);

                    // steam
                    if (on)
                    {
                        using (var steam = new Pen(c, stroke * 0.8f))
                        {
                            steam.StartCap = LineCap.Round;
                            steam.EndCap = LineCap.Round;
                            for (int i = 0; i < 3; i++)
                            {
                                float x = 7f + i * 5f;
                                g.DrawBezier(steam, x, 9.5f, x - 2.2f, 7f, x + 2.2f, 4.5f, x, 2f);
                            }
                        }
                    }
                }
            }
            return bmp;
        }

        static GraphicsPath RoundedBottom(float x, float y, float w, float h, float r)
        {
            var p = new GraphicsPath();
            p.AddLine(x, y, x + w, y);
            p.AddLine(x + w, y, x + w, y + h - r);
            p.AddArc(x + w - 2 * r, y + h - 2 * r, 2 * r, 2 * r, 0, 90);
            p.AddLine(x + w - r, y + h, x + r, y + h);
            p.AddArc(x, y + h - 2 * r, 2 * r, 2 * r, 90, 90);
            p.CloseFigure();
            return p;
        }

        public static Icon CreateIcon(int size, bool on)
        {
            using (var bmp = Draw(size, on))
            {
                IntPtr h = bmp.GetHicon();
                try
                {
                    using (var tmp = Icon.FromHandle(h)) return (Icon)tmp.Clone();
                }
                finally { Native.DestroyIcon(h); }
            }
        }

        // Multi-size .ico with PNG-compressed frames (Vista+).
        public static void SaveIco(string path)
        {
            int[] sizes = { 16, 20, 24, 32, 40, 48, 64, 256 };
            var frames = new List<byte[]>();
            foreach (int sz in sizes)
            {
                using (var bmp = Draw(sz, true))
                using (var ms = new MemoryStream())
                {
                    bmp.Save(ms, ImageFormat.Png);
                    frames.Add(ms.ToArray());
                }
            }

            using (var fs = File.Create(path))
            using (var w = new BinaryWriter(fs))
            {
                w.Write((ushort)0);            // reserved
                w.Write((ushort)1);            // type: icon
                w.Write((ushort)sizes.Length); // count
                int offset = 6 + 16 * sizes.Length;
                for (int i = 0; i < sizes.Length; i++)
                {
                    byte dim = (byte)(sizes[i] >= 256 ? 0 : sizes[i]);
                    w.Write(dim); w.Write(dim);
                    w.Write((byte)0); w.Write((byte)0); // palette, reserved
                    w.Write((ushort)1);                 // planes
                    w.Write((ushort)32);                // bpp
                    w.Write(frames[i].Length);
                    w.Write(offset);
                    offset += frames[i].Length;
                }
                foreach (var f in frames) w.Write(f);
            }
        }
    }
}
