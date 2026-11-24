// Kapeyn - keeps your Windows PC awake.
// Compiles with the csc.exe that ships with Windows (.NET Framework 4.x, C# 5) - see build.ps1.
//   Kapeyn.exe  - the tray app (GUI)
//   kapeyn.com  - the command-line tool (same code, built with /define:CLI). Because Windows runs .com before
//                 .exe, typing "kapeyn" in a terminal gets the command-line tool, while double-clicking
//                 Kapeyn.exe opens the tray app.

using System;
using System.Reflection;
using System.Threading;
using System.Windows.Forms;

[assembly: AssemblyTitle("Kapeyn")]
[assembly: AssemblyProduct("Kapeyn")]
[assembly: AssemblyDescription("Keeps your PC awake")]
[assembly: AssemblyVersion("2.3.1.0")]
[assembly: AssemblyFileVersion("2.3.1.0")]

namespace Kapeyn
{
    static class Program
    {
        internal const string MutexName = "Local\\Kapeyn.SingleInstance";
        internal const string SignalName = "Local\\Kapeyn.SecondLaunch";

        internal static string PipeName
        {
            get { return "Kapeyn." + Environment.UserName; }
        }

        [STAThread]
        static int Main(string[] args)
        {
#if CLI
            return Cli.Run(args);
#else
            // Build helper: write the app icon to disk (used by build.ps1).
            if (args.Length == 2 && args[0] == "--export-icon")
            {
                IconArt.SaveIco(args[1]);
                return 0;
            }

            // Diagnostic: send one nudge of each kind; exit code = input events Windows accepted (expect 4).
            if (args.Length == 1 && args[0] == "--nudge-test")
                return (int)Native.Nudge(NudgeMethod.Both);

            bool cliLaunch = Array.IndexOf(args, "--cli-launch") >= 0;

            bool createdNew;
            using (var mutex = new Mutex(true, MutexName, out createdNew))
            {
                if (!createdNew)
                {
                    // Already running: poke the running copy (it turns on and says hello) and quit.
                    if (!cliLaunch)
                    {
                        try
                        {
                            using (var signal = EventWaitHandle.OpenExisting(SignalName)) signal.Set();
                        }
                        catch { }
                    }
                    return 0;
                }

                using (var signal = new EventWaitHandle(false, EventResetMode.AutoReset, SignalName))
                {
                    Application.EnableVisualStyles();
                    Application.SetCompatibleTextRenderingDefault(false);
                    Application.Run(new TrayApp(signal, cliLaunch));
                }
                GC.KeepAlive(mutex);
            }
            return 0;
#endif
        }
    }
}
