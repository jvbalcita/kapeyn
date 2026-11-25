using System;
using System.Collections.Generic;
using System.Linq;

namespace Kapeyn
{
    // One reason to stay awake. Key identifies it (for snoozing), Label is what the user sees.
    sealed class Reason
    {
        public readonly string Key;
        public readonly string Label;
        public Reason(string key, string label) { Key = key; Label = label; }
    }

    /// What to do when a session finishes by itself ("When it's done").
    enum DoneAction { Nothing, Sleep, Lock, ShutDown }

    /// What a "Keep awake until" session waits for.
    enum UntilKind { None, Downloads, AppCloses, CommandEnds }

    // What happened during one Engine.Step, for the app to act on (notifications, lock, shutdown).
    sealed class StepEvents
    {
        public bool TimerEnded;      // a "Keep awake for" timer ran out
        public bool UntilEnded;      // a "Keep awake until" session finished (downloads done, app closed, command ended)
        public string Finished;      // ...what finished, as a sentence: "Downloads finished."
        public DoneAction Then;      // ...and what to do now (timer or "until" end)
        public bool WorkHoursEnded;  // work hours just ended (seen live) and "Turn Kapeyn off" is on
        public bool WasManualOn;     // ...and Kapeyn had been switched on by hand
        public bool Lock;            // ...and the computer should be locked
        public bool Shutdown;        // ...or shut down (after a countdown)
        public bool PauseStarted;    // just paused because of low battery
    }

    // Decides whether to stay awake and why. It makes no system calls and has no UI, so it can be tested:
    // the app gathers the inputs (time, calls, downloads, apps, battery), calls Step, then acts on the result.
    sealed class Engine
    {
        readonly string computer;                                   // "PC" or "Mac", for the status text
        readonly HashSet<string> snoozed = new HashSet<string>();   // automatic reasons the user turned off
        List<Reason> lastAuto = new List<Reason>();
        bool wasInWindow, batteryOverride;
        DateTime lastStep = DateTime.MinValue;
        DateTime? quietSince;                                       // downloads: when traffic last stopped

        /// "Keep awake until downloads finish" ends after this long without downloading.
        public const int DownloadQuietMinutes = 2;

        public bool ManualOn { get; private set; }
        public DateTime? ManualUntil { get; private set; }
        public int? ManualMinutes { get; private set; }             // the "Keep awake for" choice
        public UntilKind Until { get; private set; }                // the "Keep awake until" choice
        public string UntilTarget { get; private set; }             // ...the app's process name, or the command's pid
        public string UntilLabel { get; private set; }              // ...the app or command name the user sees
        /// What to do when the current timer or "until" session finishes. Resets once it's used or Kapeyn is
        /// turned off, so a "Shut down" choice never carries over to a later session.
        public DoneAction Then { get; set; }
        public bool Awake { get; private set; }
        public bool Paused { get; private set; }
        public string PauseWhy { get; private set; }
        public List<Reason> Reasons { get; private set; }

        public Engine(string computerName, bool inWorkHoursWindowNow)
        {
            computer = computerName;
            wasInWindow = inWorkHoursWindowNow;
            Reasons = new List<Reason>();
        }

        /// Switch on by hand, for a number of minutes or (null) until switched off.
        public void TurnOn(int? minutes, DateTime now)
        {
            ManualOn = true;
            ManualUntil = minutes.HasValue ? now.AddMinutes(minutes.Value) : (DateTime?)null;
            ManualMinutes = minutes;
            ClearUntil();
            snoozed.Clear();
            batteryOverride = batteryOverride || Paused; // turning on while paused overrides the battery pause
        }

        /// Switch on until something finishes: downloads (no target), an app closing (target = its process
        /// name or bundle id) or a command ending (target = its process id).
        public void TurnOnUntil(UntilKind kind, string target, string label, DateTime now)
        {
            TurnOn(null, now);
            ManualMinutes = null;
            Until = kind;
            UntilTarget = target;
            UntilLabel = label;
        }

        /// Switch off by hand. Automatic reasons active right now stay off until they end.
        public void TurnOff()
        {
            EndSession();
            foreach (var r in lastAuto) snoozed.Add(r.Key);
            batteryOverride = false;
        }

        void ClearUntil()
        {
            Until = UntilKind.None;
            UntilTarget = null;
            UntilLabel = null;
            quietSince = null;
        }

        void EndSession()
        {
            ManualOn = false;
            ManualUntil = null;
            ClearUntil();
            Then = DoneAction.Nothing;
        }

        /// "sleep", "lock", "shutdown" / "shut down", "nothing" / "none" -> the action. False if not recognized.
        public static bool TryParseThen(string text, out DoneAction action)
        {
            action = DoneAction.Nothing;
            switch ((text ?? "").Trim().ToLowerInvariant().Replace(" ", "").Replace("-", ""))
            {
                case "nothing": case "none": case "off": action = DoneAction.Nothing; return true;
                case "sleep": case "suspend": action = DoneAction.Sleep; return true;
                case "lock": action = DoneAction.Lock; return true;
                case "shutdown": case "poweroff": action = DoneAction.ShutDown; return true;
                default: return false;
            }
        }

        /// The action as the user reads it: "sleep", "lock", "shut down", "do nothing".
        public static string ThenText(DoneAction action)
        {
            switch (action)
            {
                case DoneAction.Sleep: return "sleep";
                case DoneAction.Lock: return "lock";
                case DoneAction.ShutDown: return "shut down";
                default: return "do nothing";
            }
        }

        /// What the current "until" session is waiting for: "downloads finish", "Photoshop closes".
        public string UntilText()
        {
            switch (Until)
            {
                case UntilKind.Downloads: return "downloads finish";
                case UntilKind.AppCloses: return UntilLabel + " closes";
                case UntilKind.CommandEnds: return UntilLabel + " finishes";
                default: return null;
            }
        }

        public bool IsSnoozed(string key) { return snoozed.Contains(key); }
        public void Unsnooze(string key) { snoozed.Remove(key); }

        /// Start work-hours tracking from "now" (after waking from sleep or saving new hours),
        /// so an end that wasn't seen live never triggers turning off, locking or shutting down.
        public void Rebaseline(bool inWorkHoursWindowNow) { wasInWindow = inWorkHoursWindowNow; }

        /// One evaluation. inWindow = inside the work-hours window (whether or not the schedule is on);
        /// auto = the other automatic reasons active now (call, downloading, apps, kapeyn run);
        /// untilActive = what a "Keep awake until" session waits for is still going (downloading, app open,
        /// command running).
        public StepEvents Step(Settings s, DateTime now, bool inWindow, IEnumerable<Reason> auto,
                               bool onBattery, int batteryPercent, bool untilActive = true)
        {
            var ev = new StepEvents();
            bool wasPaused = Paused;

            if (ManualOn && ManualUntil.HasValue && now >= ManualUntil.Value)
            {
                ev.TimerEnded = true;
                ev.Finished = "Timer finished.";
                ev.Then = Then;
                EndSession();
            }
            else if (ManualOn && Until != UntilKind.None)
            {
                bool done;
                if (Until == UntilKind.Downloads)
                {
                    // Downloads come in bursts (one file after another), so wait for a quiet stretch.
                    if (untilActive) quietSince = null;
                    else if (!quietSince.HasValue) quietSince = now;
                    done = quietSince.HasValue && (now - quietSince.Value).TotalMinutes >= DownloadQuietMinutes;
                }
                else done = !untilActive;

                if (done)
                {
                    ev.UntilEnded = true;
                    ev.Finished = Until == UntilKind.Downloads ? "Downloads finished."
                                : Until == UntilKind.AppCloses ? UntilLabel + " closed."
                                : UntilLabel + " finished.";
                    ev.Then = Then;
                    EndSession();
                }
            }

            // Work hours ended? Only when seen live (the previous step was under 2 minutes ago), only when the
            // schedule is on, and only for a real end of the window, never for switching the schedule off.
            bool fresh = (now - lastStep).TotalMinutes < 2;
            lastStep = now;
            if (s.ScheduleEnabled && s.ScheduleEndTurnsOff && wasInWindow && !inWindow && fresh)
            {
                ev.WorkHoursEnded = true;
                ev.WasManualOn = ManualOn;
                ev.Shutdown = s.EndShutdown;
                ev.Lock = s.EndLock && !s.EndShutdown;
                EndSession(); // work hours have their own end action, so any "When it's done" choice is dropped
            }
            wasInWindow = inWindow;

            var all = new List<Reason>();
            if (s.ScheduleEnabled && inWindow) all.Add(new Reason("hours", "work hours"));
            all.AddRange(auto);
            lastAuto = all;

            // Forget snoozes for reasons that have ended, so the next call/download wakes Kapeyn again.
            var keys = new HashSet<string>(all.Select(r => r.Key));
            snoozed.RemoveWhere(k => !keys.Contains(k));

            var active = new List<Reason>();
            if (ManualOn)
            {
                string label = ManualUntil.HasValue ? Format.Remaining(ManualUntil.Value - now) + " left"
                             : Until != UntilKind.None ? "until " + UntilText()
                             : "turned on";
                if (Then != DoneAction.Nothing && (ManualUntil.HasValue || Until != UntilKind.None))
                    label += ", then " + ThenText(Then);
                active.Add(new Reason("manual", label));
            }
            active.AddRange(all.Where(r => !snoozed.Contains(r.Key)));
            Reasons = active;
            bool wanted = active.Count > 0;

            // Low battery pause (clicking the cup overrides it until plugged in).
            Paused = false;
            PauseWhy = null;
            if (!onBattery) batteryOverride = false;
            if (wanted && s.PauseBatteryPercent > 0 && onBattery && batteryPercent >= 0 &&
                batteryPercent < s.PauseBatteryPercent && !batteryOverride)
            {
                Paused = true;
                PauseWhy = "battery at " + batteryPercent + "%";
            }
            ev.PauseStarted = Paused && !wasPaused;

            Awake = wanted && !Paused;
            return ev;
        }

        public string StatusLine()
        {
            if (Paused) return "Paused - " + PauseWhy;
            if (!Awake) return "Off - " + computer + " may sleep";
            // Just "Awake" when the only reason is that the user turned it on.
            if (Reasons.Count == 1 && Reasons[0].Key == "manual" && !ManualUntil.HasValue && Until == UntilKind.None)
                return "Awake";
            return "Awake - " + string.Join(", ", Reasons.Select(r => r.Label).ToArray());
        }
    }
}
