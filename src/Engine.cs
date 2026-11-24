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

    // What happened during one Engine.Step, for the app to act on (notifications, lock, shutdown).
    sealed class StepEvents
    {
        public bool TimerEnded;      // a "Keep awake for" timer ran out
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

        public bool ManualOn { get; private set; }
        public DateTime? ManualUntil { get; private set; }
        public int? ManualMinutes { get; private set; }             // the "Keep awake for" choice
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
            snoozed.Clear();
            batteryOverride = batteryOverride || Paused; // turning on while paused overrides the battery pause
        }

        /// Switch off by hand. Automatic reasons active right now stay off until they end.
        public void TurnOff()
        {
            ManualOn = false;
            ManualUntil = null;
            foreach (var r in lastAuto) snoozed.Add(r.Key);
            batteryOverride = false;
        }

        public bool IsSnoozed(string key) { return snoozed.Contains(key); }
        public void Unsnooze(string key) { snoozed.Remove(key); }

        /// Start work-hours tracking from "now" (after waking from sleep or saving new hours),
        /// so an end that wasn't seen live never triggers turning off, locking or shutting down.
        public void Rebaseline(bool inWorkHoursWindowNow) { wasInWindow = inWorkHoursWindowNow; }

        /// One evaluation. inWindow = inside the work-hours window (whether or not the schedule is on);
        /// auto = the other automatic reasons active now (call, downloading, apps, kapeyn run).
        public StepEvents Step(Settings s, DateTime now, bool inWindow, IEnumerable<Reason> auto,
                               bool onBattery, int batteryPercent)
        {
            var ev = new StepEvents();
            bool wasPaused = Paused;

            if (ManualOn && ManualUntil.HasValue && now >= ManualUntil.Value)
            {
                ManualOn = false;
                ManualUntil = null;
                ev.TimerEnded = true;
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
                ManualOn = false;
                ManualUntil = null;
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
                active.Add(new Reason("manual", ManualUntil.HasValue ? Format.Remaining(ManualUntil.Value - now) + " left" : "turned on"));
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
            if (Reasons.Count == 1 && Reasons[0].Key == "manual" && !ManualUntil.HasValue) return "Awake";
            return "Awake - " + string.Join(", ", Reasons.Select(r => r.Label).ToArray());
        }
    }
}
