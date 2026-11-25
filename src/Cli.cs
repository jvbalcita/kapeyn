using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;

namespace Kapeyn
{
    // The "kapeyn" command (kapeyn.com). Talks to the tray app, starting it if needed.
    static class Cli
    {
        public const string HelpText =
            "kapeyn                      Show what Kapeyn is doing\n" +
            "kapeyn on                   Keep awake until you turn it off\n" +
            "kapeyn on 30m | 2h | 1h30m  Keep awake for a while\n" +
            "kapeyn off                  Turn off\n" +
            "kapeyn toggle               Switch on/off\n" +
            "kapeyn run <command...>     Keep awake until the command finishes, e.g.  kapeyn run npm run build\n" +
            "kapeyn doctor               Check what locks or sleeps this computer\n" +
            "kapeyn guide                Open the full guide (also in the menu: Help)\n";

        public static int Run(string[] args)
        {
            try
            {
                if (args.Length == 0) return Print(Send("status"));
                string verb = args[0].ToLowerInvariant();
                switch (verb)
                {
                    case "help": case "-h": case "--help": case "/?":
                        Console.Write(HelpText);
                        return 0;
                    case "on":
                        if (args.Length > 1)
                        {
                            int minutes;
                            if (!TryParseDuration(string.Join("", args.Skip(1).ToArray()), out minutes))
                            {
                                Console.Error.WriteLine("kapeyn: couldn't understand \"" + string.Join(" ", args.Skip(1).ToArray()) + "\". Try 30m, 2h or 1h30m.");
                                return 2;
                            }
                            return Print(Send("on " + minutes));
                        }
                        return Print(Send("on"));
                    case "off": case "toggle": case "status": case "doctor": case "guide":
                        return Print(Send(verb));
                    case "run":
                        return RunCommand(args.Skip(1).ToArray());
                    default:
                        Console.Error.WriteLine("kapeyn: unknown command \"" + args[0] + "\"\n");
                        Console.Error.Write(HelpText);
                        return 2;
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("kapeyn: " + ex.Message);
                return 1;
            }
        }

        static int Print(string reply)
        {
            Console.WriteLine(reply.TrimEnd());
            return reply.StartsWith("error", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
        }

        /// Sends a command to the tray app, starting it first if it isn't running.
        static string Send(string command)
        {
            string reply;
            if (IpcClient.TrySend(command, 500, out reply)) return reply;

            string exe = FindApp();
            if (exe == null)
                throw new ApplicationException("Kapeyn isn't running and Kapeyn.exe couldn't be found. Reinstall Kapeyn, or start it from the Start menu.");
            Process.Start(new ProcessStartInfo(exe, "--cli-launch") { UseShellExecute = true });

            for (int i = 0; i < 40; i++)
            {
                Thread.Sleep(250);
                if (IpcClient.TrySend(command, 500, out reply)) return reply;
            }
            throw new ApplicationException("Kapeyn is running but didn't answer. If an older version is running, exit it from the tray and try again.");
        }

        /// Where Kapeyn.exe is: next to this command's real file (following winget's link), then the usual
        /// install folders. Returns null if it isn't found.
        public static string FindApp()
        {
            var candidates = new List<string>();
            try
            {
                string self = Native.RealPath(Process.GetCurrentProcess().MainModule.FileName);
                candidates.Add(Path.Combine(Path.GetDirectoryName(self), "Kapeyn.exe"));
            }
            catch { }
            candidates.Add(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Kapeyn.exe"));
            string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            candidates.Add(Path.Combine(local, @"Programs\Kapeyn\Kapeyn.exe"));
            try
            {
                string winget = Path.Combine(local, @"Microsoft\WinGet\Packages");
                if (Directory.Exists(winget))
                    foreach (string dir in Directory.GetDirectories(winget, "Kapeyn.Kapeyn*"))
                        candidates.Add(Path.Combine(dir, "Kapeyn.exe"));
            }
            catch { }
            return candidates.FirstOrDefault(File.Exists);
        }

        static int RunCommand(string[] cmd)
        {
            if (cmd.Length > 0 && cmd[0] == "--") cmd = cmd.Skip(1).ToArray();
            if (cmd.Length == 0)
            {
                Console.Error.WriteLine("usage: kapeyn run <command...>");
                return 2;
            }

            int me = Process.GetCurrentProcess().Id;
            string label = Path.GetFileNameWithoutExtension(cmd[0]);
            if (label.Length > 40) label = label.Substring(0, 40);

            bool held = false;
            try
            {
                Send("hold " + me + " " + label);
                held = true;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("kapeyn: " + ex.Message + " Keeping the PC awake from here instead.");
                Native.SetThreadExecutionState(Native.ES_CONTINUOUS | Native.ES_SYSTEM_REQUIRED | Native.ES_DISPLAY_REQUIRED);
            }

            string line = string.Join(" ", cmd.Select(Quote).ToArray());
            var psi = new ProcessStartInfo("cmd.exe", "/d /s /c \"" + line + "\"") { UseShellExecute = false };
            int code;
            using (var p = Process.Start(psi))
            {
                p.WaitForExit();
                code = p.ExitCode;
            }

            if (held)
            {
                string ignored;
                IpcClient.TrySend("release " + me, 1000, out ignored);
            }
            return code;
        }

        static string Quote(string a)
        {
            if (a.Length > 0 && a.IndexOfAny(new[] { ' ', '\t', '"', '&', '|', '<', '>', '^', '(', ')' }) < 0) return a;
            return "\"" + a.Replace("\"", "\\\"") + "\"";
        }

        public static bool TryParseDuration(string text, out int minutes)
        {
            minutes = 0;
            string s = text.Trim().ToLowerInvariant().Replace(" ", "");
            int n;
            if (int.TryParse(s, out n)) { minutes = n; return n > 0; }
            var m = Regex.Match(s, @"^(?:(\d+)h(?:ours?|rs?)?)?(?:(\d+)m(?:in(?:utes?|s)?)?)?$");
            if (!m.Success || (!m.Groups[1].Success && !m.Groups[2].Success)) return false;
            int h = m.Groups[1].Success ? int.Parse(m.Groups[1].Value) : 0;
            int mi = m.Groups[2].Success ? int.Parse(m.Groups[2].Value) : 0;
            minutes = h * 60 + mi;
            return minutes > 0;
        }
    }
}
