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
            "kapeyn until downloads      Keep awake until downloads finish\n" +
            "kapeyn until <app>          Keep awake until an app closes, e.g.  kapeyn until \"Premiere Pro\"\n" +
            "kapeyn run <command...>     Keep awake until the command finishes, e.g.  kapeyn run npm run build\n" +
            "kapeyn then sleep | lock | shutdown | nothing\n" +
            "                            What to do when the time is up, or the download, app or command is done.\n" +
            "                            Or add it to the others:  kapeyn on 2h --then sleep\n" +
            "                                                      kapeyn run --then shutdown npm run build\n" +
            "kapeyn off                  Turn off\n" +
            "kapeyn toggle               Switch on/off\n" +
            "kapeyn doctor               Check what locks or sleeps this computer\n" +
            "kapeyn guide                Open the full guide (also in the menu: Help)\n";

        public static int Run(string[] args)
        {
            try
            {
                if (args.Length == 0) return Print(Send("status"));
                string verb = args[0].ToLowerInvariant();
                string then;
                switch (verb)
                {
                    case "help": case "-h": case "--help": case "/?":
                        Console.Write(HelpText);
                        return 0;
                    case "on":
                        if (!TakeThen(ref args, false, out then)) return 2;
                        string reply;
                        if (args.Length > 1)
                        {
                            int minutes;
                            if (!TryParseDuration(string.Join("", args.Skip(1).ToArray()), out minutes))
                            {
                                Console.Error.WriteLine("kapeyn: couldn't understand \"" + string.Join(" ", args.Skip(1).ToArray()) + "\". Try 30m, 2h or 1h30m.");
                                return 2;
                            }
                            reply = Send("on " + minutes);
                        }
                        else reply = Send("on");
                        return Print(then != null ? Send("then " + then) : reply);
                    case "until":
                        if (!TakeThen(ref args, false, out then)) return 2;
                        string[] what = args.Skip(1).ToArray();
                        if (what.Length > 0 && what[0].ToLowerInvariant() == "app") what = what.Skip(1).ToArray();
                        if (what.Length == 0)
                        {
                            Console.Error.WriteLine("usage: kapeyn until downloads | kapeyn until <app>   (add --then sleep, lock or shutdown)");
                            return 2;
                        }
                        string w = what[0].ToLowerInvariant();
                        string untilReply = what.Length == 1 && (w == "downloads" || w == "download")
                            ? Send("until downloads")
                            : Send("until app " + string.Join(" ", what));
                        if (then == null || untilReply.StartsWith("error", StringComparison.OrdinalIgnoreCase)) return Print(untilReply);
                        return Print(Send("then " + then));
                    case "then":
                        DoneAction action;
                        string text = string.Join(" ", args.Skip(1).ToArray());
                        if (!Engine.TryParseThen(text, out action))
                        {
                            Console.Error.WriteLine("usage: kapeyn then sleep | lock | shutdown | nothing");
                            return 2;
                        }
                        return Print(Send("then " + ThenWord(action)));
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

        /// The word the app understands for an action: "sleep", "lock", "shutdown", "nothing".
        public static string ThenWord(DoneAction a)
        {
            switch (a)
            {
                case DoneAction.Sleep: return "sleep";
                case DoneAction.Lock: return "lock";
                case DoneAction.ShutDown: return "shutdown";
                default: return "nothing";
            }
        }

        /// Takes "--then X" or "--then=X" out of args (leadingOnly: only right at the start, for "run", so the
        /// command's own options are left alone). then = the action word, or null if there's no --then.
        /// Prints a message and returns false if X isn't an action.
        public static bool TakeThen(ref string[] args, bool leadingOnly, out string then)
        {
            then = null;
            var list = args.ToList();
            int first = leadingOnly ? 0 : 1; // "run" gets the args after "run"; the others still have the verb
            for (int i = first; i < list.Count; i++)
            {
                string a = list[i];
                string value = null;
                int take = 0;
                if (a.StartsWith("--then=", StringComparison.OrdinalIgnoreCase)) { value = a.Substring(7); take = 1; }
                else if (string.Equals(a, "--then", StringComparison.OrdinalIgnoreCase))
                {
                    take = 2;
                    value = i + 1 < list.Count ? list[i + 1] : "";
                    // "--then shut down"
                    if (value.ToLowerInvariant() == "shut" && i + 2 < list.Count && list[i + 2].ToLowerInvariant() == "down")
                    {
                        value = "shutdown";
                        take = 3;
                    }
                }
                if (take == 0)
                {
                    if (leadingOnly) break;
                    continue;
                }
                DoneAction action;
                if (!Engine.TryParseThen(value, out action))
                {
                    Console.Error.WriteLine("kapeyn: --then needs sleep, lock, shutdown or nothing" +
                                            (value.Length > 0 ? " (not \"" + value + "\")" : "") + ".");
                    return false;
                }
                then = ThenWord(action);
                list.RemoveRange(i, Math.Min(take, list.Count - i));
                break;
            }
            args = list.ToArray();
            return true;
        }

        static int RunCommand(string[] cmd)
        {
            string then;
            if (!TakeThen(ref cmd, true, out then)) return 2;
            if (cmd.Length > 0 && cmd[0] == "--") cmd = cmd.Skip(1).ToArray();
            if (cmd.Length == 0)
            {
                Console.Error.WriteLine("usage: kapeyn run [--then sleep|lock|shutdown] <command...>");
                return 2;
            }

            int me = Process.GetCurrentProcess().Id;
            string label = Path.GetFileNameWithoutExtension(cmd[0]);
            if (label.Length > 40) label = label.Substring(0, 40);

            bool held = false;
            try
            {
                if (then != null)
                {
                    // The app watches this process and does the action once it ends.
                    string reply = Send("until pid " + me + " " + label);
                    if (!reply.StartsWith("error", StringComparison.OrdinalIgnoreCase)) reply = Send("then " + then);
                    string doing = then == "sleep" ? "put the PC to sleep" : then == "lock" ? "lock the PC" : "shut down the PC";
                    Console.Error.WriteLine("kapeyn: " + (then == "nothing"
                        ? "keeping the PC awake until " + label + " finishes."
                        : "when " + label + " finishes, Kapeyn will " + doing + "."));
                    // Stopped with Ctrl+C: end quietly instead of sleeping or shutting down.
                    Console.CancelKeyPress += delegate
                    {
                        string ignored;
                        IpcClient.TrySend("then nothing", 1000, out ignored);
                    };
                }
                else Send("hold " + me + " " + label);
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

            if (held && then == null)
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
