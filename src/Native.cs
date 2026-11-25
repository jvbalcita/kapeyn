using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace Kapeyn
{
    static class Native
    {
        // ---- power requests ------------------------------------------------------------------
        public const uint ES_SYSTEM_REQUIRED = 0x00000001;
        public const uint ES_DISPLAY_REQUIRED = 0x00000002;
        public const uint ES_CONTINUOUS = 0x80000000;

        [DllImport("kernel32.dll")]
        public static extern uint SetThreadExecutionState(uint esFlags);

        [DllImport("powrprof.dll")]
        static extern uint CallNtPowerInformation(int level, IntPtr input, uint inputLength, out uint output, uint outputLength);

        /// System-wide execution state (any process): bit 1 = system required, bit 2 = display required.
        public static uint SystemExecutionState()
        {
            uint state;
            return CallNtPowerInformation(16 /* SystemExecutionState */, IntPtr.Zero, 0, out state, 4) == 0 ? state : 0;
        }

        // ---- power plan ----------------------------------------------------------------------
        [DllImport("powrprof.dll")]
        static extern uint PowerGetActiveScheme(IntPtr userRootPowerKey, out IntPtr activePolicyGuid);

        [DllImport("powrprof.dll")]
        static extern uint PowerReadACValueIndex(IntPtr rootPowerKey, ref Guid schemeGuid, ref Guid subGroupGuid, ref Guid settingGuid, out uint value);

        [DllImport("powrprof.dll")]
        static extern uint PowerReadDCValueIndex(IntPtr rootPowerKey, ref Guid schemeGuid, ref Guid subGroupGuid, ref Guid settingGuid, out uint value);

        [DllImport("kernel32.dll")]
        static extern IntPtr LocalFree(IntPtr hMem);

        /// Reads a value from the active power plan. Returns -1 if unavailable.
        public static int ReadPowerSetting(bool pluggedIn, Guid subGroup, Guid setting)
        {
            IntPtr p;
            if (PowerGetActiveScheme(IntPtr.Zero, out p) != 0) return -1;
            try
            {
                var scheme = (Guid)Marshal.PtrToStructure(p, typeof(Guid));
                uint v;
                uint r = pluggedIn
                    ? PowerReadACValueIndex(IntPtr.Zero, ref scheme, ref subGroup, ref setting, out v)
                    : PowerReadDCValueIndex(IntPtr.Zero, ref scheme, ref subGroup, ref setting, out v);
                return r == 0 ? (int)v : -1;
            }
            finally { LocalFree(p); }
        }

        // ---- screen saver --------------------------------------------------------------------
        public const uint SPI_GETSCREENSAVETIMEOUT = 0x000E;
        public const uint SPI_GETSCREENSAVEACTIVE = 0x0010;
        public const uint SPI_GETSCREENSAVESECURE = 0x0076;

        [DllImport("user32.dll")]
        public static extern bool SystemParametersInfo(uint action, uint param, ref int value, uint winIni);

        // ---- idle time -----------------------------------------------------------------------
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

        // ---- simulated input -----------------------------------------------------------------
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

        public static int NudgeEventCount(NudgeMethod method)
        {
            return method == NudgeMethod.Both ? 4 : 2;
        }

        /// Sends a tiny invisible input. Returns how many input events Windows accepted.
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

        // ---- global hotkey -------------------------------------------------------------------
        public const uint MOD_ALT = 0x0001;
        public const uint MOD_CONTROL = 0x0002;
        public const uint MOD_NOREPEAT = 0x4000;

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

        [DllImport("user32.dll")]
        public static extern bool UnregisterHotKey(IntPtr hWnd, int id);

        // ---- misc ----------------------------------------------------------------------------
        [DllImport("user32.dll")]
        public static extern bool DestroyIcon(IntPtr hIcon);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool LockWorkStation();

        [DllImport("user32.dll")]
        static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

        /// Puts the PC to sleep. PCs with Modern Standby (most newer laptops) may refuse a direct sleep
        /// request; turning the screen off puts those to sleep instead.
        public static void Sleep()
        {
            bool ok = false;
            try { ok = System.Windows.Forms.Application.SetSuspendState(System.Windows.Forms.PowerState.Suspend, false, false); }
            catch { }
            if (!ok)
                PostMessage((IntPtr)0xFFFF /* HWND_BROADCAST */, 0x0112 /* WM_SYSCOMMAND */,
                            (IntPtr)0xF170 /* SC_MONITORPOWER */, (IntPtr)2 /* off */);
        }

        // ---- real file paths (through symbolic links) ------------------------------------------
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern Microsoft.Win32.SafeHandles.SafeFileHandle CreateFile(string name, uint access, uint share,
            IntPtr security, uint disposition, uint flags, IntPtr template);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern uint GetFinalPathNameByHandle(Microsoft.Win32.SafeHandles.SafeFileHandle file,
            System.Text.StringBuilder path, uint length, uint flags);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.I1)] // returns a 1-byte BOOLEAN, not a 4-byte BOOL
        public static extern bool CreateSymbolicLink(string link, string target, uint flags);

        /// The real location of a file, following symbolic links (winget runs portable apps through
        /// links in its own folder). Returns the path unchanged if it can't be resolved.
        public static string RealPath(string path)
        {
            try
            {
                using (var h = CreateFile(path, 0, 7 /* share all */, IntPtr.Zero, 3 /* OPEN_EXISTING */,
                                          0x02000000 /* FILE_FLAG_BACKUP_SEMANTICS */, IntPtr.Zero))
                {
                    if (h.IsInvalid) return path;
                    var sb = new System.Text.StringBuilder(1024);
                    uint n = GetFinalPathNameByHandle(h, sb, (uint)sb.Capacity, 0);
                    if (n == 0 || n >= sb.Capacity) return path;
                    string s = sb.ToString();
                    if (s.StartsWith(@"\\?\UNC\")) return @"\\" + s.Substring(8);
                    if (s.StartsWith(@"\\?\")) return s.Substring(4);
                    return s;
                }
            }
            catch { return path; }
        }
    }
}
