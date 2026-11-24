%% Kapeyn's in-app guide. Both apps read this one file (Windows embeds it, macOS copies it into the app).
%% Format: "# " starts a topic, "## " a heading, "- " a bullet, "1. " a numbered step, "> " a tip,
%% **bold** and `code` inline. Lines starting "[win] " or "[mac] " appear only on that platform.
%% Tokens: {computer} {Computer} {shortcut} {tray} {rightclick} {RightClick} {startup} {version}. "%% " lines are comments.

# Getting started
Kapeyn keeps your {computer} awake: no sleep, no screen lock and no "Away" status in chat apps, for as long as you want.

## Start in 3 steps
1. Find the **coffee cup** in the {tray}.
[win] If you don't see it, click the **^** arrow on the taskbar. Drag the cup onto the taskbar to keep it visible.
[mac] If you don't see it, the menu bar may be full. Quit a few menu bar apps, or hold **Command** and drag the cup further left.
2. **Click the cup** to turn Kapeyn on. A steaming orange cup means your {computer} is staying awake.
3. **Click it again** to turn Kapeyn off. The cup becomes a grey outline.

That's all you need. Everything else is optional.

## Good to know
- Press **{shortcut}** anywhere to switch Kapeyn on or off without touching the mouse.
- {RightClick} the cup to see every option.
- Only one Kapeyn runs at a time. Opening it again simply turns it on.
- Kapeyn starts by itself when you sign in, so you can set it and forget it.

> Not sure your {computer} is protected? Open **Lock doctor** from the menu. It checks everything that could lock or sleep your {computer} and tells you, in plain words, whether Kapeyn covers it.

# The cup and the menu
## What the cup tells you
- **Steaming orange cup:** awake. Your {computer} won't sleep, dim or lock.
- **Grey outline:** off. Your {computer} follows its normal sleep and lock settings.
- **Hover over the cup** to see why it's awake, for example "Awake - on a call (Teams)" or "Awake - 1 h 20 min left".

## The menu, top to bottom
{RightClick} the cup to open it.
- **Status:** what Kapeyn is doing now, and when it last kept you active.
- **Turn on / Turn off:** the same as clicking the cup.
- **Keep awake for:** stay awake for a set time (15 minutes to 8 hours), then switch off by itself.
- **Keep awake automatically:** switch on by itself during calls, downloads, chosen apps or work hours.
- **Settings:** fine-tune how Kapeyn works.
- **Lock doctor:** check what could lock or sleep your {computer}.
- **Command line:** control Kapeyn by typing commands.
- **Help:** this guide.
[win] - **Exit:** close Kapeyn completely.
[mac] - **Quit Kapeyn:** close Kapeyn completely.

[win] > In **Settings** and **Keep awake automatically**, the menu stays open after each click, so you can change several things in a row. Click anywhere else to close it.

# Keep awake for a while
Need your {computer} awake only for a meeting, a presentation or a long download?

1. {RightClick} the cup.
2. Choose **Keep awake for**.
3. Pick a time: **15 minutes**, **30 minutes**, **1 hour**, **2 hours**, **4 hours** or **8 hours**.

Your choice gets a **check mark** and shows the time left, for example "1 hour (42 min left)". You can also hover over the cup to see it. Kapeyn switches off by itself when the time is up. Pick another time at any point to restart the countdown.

## Other ways
- **Until I turn it off:** the same submenu, or just click the cup.
- **From the command line:** `kapeyn on 45m`, `kapeyn on 2h` or `kapeyn on 1h30m`.
- **Stop early:** click the cup, press **{shortcut}**, or run `kapeyn off`.

# Automatic modes
Kapeyn can switch itself on when you need it, and off again when you don't. Find these under **Keep awake automatically**. A check mark means the option is on.

## While I'm on a call
Stays awake whenever an app is using your microphone: Teams, Zoom, Google Meet, Slack huddles and others. On by default.
[mac] - A headset that combines speakers and microphone may also count while you're only listening to music.

## While downloading or streaming
Stays awake while your {computer} is steadily receiving data (large downloads, video streams), and for one more minute after it stops. Short bursts such as background syncing don't count. On by default.

## While these apps are open
Choose apps that should keep your {computer} awake whenever they're open, such as a video editor, a remote desktop or a virtual machine.
1. Open the app you want to add.
2. Go to **Keep awake automatically > While these apps are open > Add an open app**.
3. Click the app. It now appears in the list with a check mark.

To remove an app, click it in the same list.

## During work hours
Stays awake on the days and hours you choose. See **Work hours**.

## Turning Kapeyn off during an automatic mode
If you turn Kapeyn off during a call or a download, it stays off until that call or download ends. The next one turns it on again.

# Work hours
Keep your {computer} awake on a schedule, for example Monday to Friday, 1:00 PM to 10:00 PM.

## Set your hours
1. {RightClick} the cup and choose **Keep awake automatically > Set work hours...**
2. Tick **Keep my {computer} awake during work hours**.
3. Tick your **days**.
4. Set **From** and **to**. Any time works, to the minute.
5. Click **Save**.

The menu now shows your hours, for example "During work hours (Mon-Fri, 1:00 PM - 10:00 PM)". To pause the schedule without losing it, click that line to remove its check mark.

## Night shifts
If the end time is earlier than the start time, your hours run overnight, for example 10:00 PM to 6:00 AM. A night shift belongs to the day it starts: with Friday ticked, Friday 10 PM to Saturday 6 AM is covered.

## When work hours end
- **Turn Kapeyn off** (on by default): if you switched Kapeyn on yourself during the day, it switches off at the end of your hours, so it doesn't keep your {computer} awake all night.
- **Also lock the {computer}** (off by default): locks your screen at the end of your hours.
- **Also shut down the {computer}** (off by default): shows a **60-second countdown** with a **Cancel** button, then shuts down normally. Apps with unsaved work can still stop the shutdown.
[mac] - The first time, macOS asks whether Kapeyn may control "System Events". Click **OK**, or Kapeyn can't shut down.

> Lock and shut down only happen when the end of your hours passes while your {computer} is on. If it was asleep at that moment, nothing happens when you wake it up.

# Settings explained
{RightClick} the cup and choose **Settings** to find these. The defaults suit most people.

## Keep screen on
Stops the display from turning off while Kapeyn is awake. On by default.

## Prevent screen lock & Away status
Many work computers lock after a few minutes without keyboard or mouse use, and chat apps then show you as "Away". When you've been idle for a while, Kapeyn gives a tiny, invisible sign of life so that doesn't happen. On by default.
[win] - Kapeyn moves the mouse pointer by one pixel and straight back. You won't see it move.
[mac] - Kapeyn tells macOS that you're active. This needs no permissions.

[win] ## Activity method
[win] How Kapeyn gives that sign of life:
[win] - **Mouse wiggle** (default): works with almost everything.
[win] - **F15 key press:** an invisible key that no app uses. This is what the "Caffeine" app does.
[win] - **Both:** the most robust. Try this if your screen still locks.

[mac] ## Also nudge the mouse (Teams/Slack)
[mac] Off by default. Turn it on if Teams or Slack still show you as Away. Kapeyn then also moves the pointer by one pixel and back.
[mac] 1. Click the setting. macOS asks for permission.
[mac] 2. Open **System Settings > Privacy & Security > Accessibility** and switch on **Kapeyn**.
[mac] If you reinstall Kapeyn, you may need to switch that permission off and on again.

## Nudge when idle for
How long you need to be idle before Kapeyn gives a sign of life.
- **Auto (recommended):** Kapeyn reads your {computer}'s lock timers and picks half of the shortest one (between 30 seconds and 4 minutes). It shows its choice in the menu.
- **30 seconds to 4 minutes:** a fixed time, if you prefer.

## Let the screen turn off when I lock the {computer}
When you lock your {computer} yourself, Kapeyn lets the screen go dark but keeps the {computer} awake, so downloads and calls carry on. On by default.

## Pause on low battery
On battery power, Kapeyn pauses below the level you choose (20% by default), so it doesn't drain your battery. Clicking the cup overrides the pause until you plug in. Choose **Never** to turn this off.

## Shortcut {shortcut}
Lets you switch Kapeyn on or off from anywhere. If another app already uses this shortcut, the menu says so.

## Turn on when Kapeyn starts
Kapeyn is already on when it starts (for example when you sign in). On by default.

## {startup}
Starts Kapeyn automatically when you sign in. On after installing.

# Lock doctor
Lock doctor checks everything that could lock, blank or sleep your {computer}, including rules set by your organization, and tells you whether Kapeyn covers each one.

## Run it
{RightClick} the cup and choose **Lock doctor**, or type `kapeyn doctor` in a terminal.

## Read the report
- **Timers:** each thing that could lock or sleep your {computer}, how soon it happens, and whether it's **[set by your organization]**.
- **[OK] Covered:** Kapeyn handles this one.
- **[!] Not covered:** something needs your attention. The report says exactly what to change.
- **[i]** extra information, for example a tip about chat apps.
- **Self-test:** Kapeyn tries its sign of life and checks that the {computer} accepted it.
- **Verdict:** the summary. "[OK] Kapeyn covers every timer found" means you're all set.

Use **Copy** to paste the report into a message, for example to your IT team.

# Command line
Control Kapeyn from a terminal. The command is `kapeyn`.

## Commands
- `kapeyn` shows what Kapeyn is doing.
- `kapeyn on` keeps your {computer} awake until you turn it off.
- `kapeyn on 30m`, `kapeyn on 2h` or `kapeyn on 1h30m` keeps it awake for a while.
- `kapeyn off` turns Kapeyn off.
- `kapeyn toggle` switches between on and off.
- `kapeyn run <command>` keeps your {computer} awake exactly as long as the command runs, for example `kapeyn run npm run build`. It then hands back the command's result.
- `kapeyn doctor` runs the Lock doctor.
- `kapeyn guide` opens this guide.
- `kapeyn help` lists the commands.

## Get the command
[win] - If you installed Kapeyn with the one-line install or **install.ps1**, the command is ready. Open a **new** PowerShell or Command Prompt window and type `kapeyn`.
[win] - If you only downloaded **Kapeyn.exe**, install it properly by pasting this into PowerShell:
[win] `irm https://github.com/jvbalcita/kapeyn/releases/latest/download/install.ps1 | iex`
[mac] - If you installed with Homebrew, the one-line install or `bash install.sh`, the command is usually ready. Open a new Terminal window and type `kapeyn`.
[mac] - Otherwise, choose **Command line...** in Kapeyn's menu and click **Install command**. macOS asks for your password once.

## Update Kapeyn
[win] Run the one-line install again. It replaces the app and keeps your settings.
[mac] Run the one-line install again, or `brew upgrade kapeyn` if you used Homebrew. Your settings are kept. Afterwards, allow Kapeyn again under **Privacy & Security > Accessibility** if you use the mouse nudge.

If Kapeyn isn't running, the command starts it for you.

# Privacy & safety
## What Kapeyn does
- Asks your {computer} not to sleep or turn off the screen while Kapeyn is on.
- When you're idle, gives a tiny sign of life so the screen doesn't lock.
- Checks whether your microphone is in use (not what it hears), how much data your {computer} is receiving (not what it is), and which apps are open, only for the automatic modes you've turned on.
- Stores its settings on your {computer} only.

## What Kapeyn never does
- It never records, listens or sends anything anywhere. It doesn't use the internet at all.
- It never types, clicks or moves the mouse in a way you can see.
- It never needs administrator rights.
- It never locks or shuts down your {computer} unless you turned that on in **Work hours**.

## Use it responsibly
Screen-lock timers on work computers are often a security rule. Lock your screen yourself (or let Kapeyn's **Work hours** lock it) when you step away, and follow your organization's policy.

# Troubleshooting
## My screen still locks
1. Run **Lock doctor**. It usually names the exact problem.
2. Make sure **Settings > Prevent screen lock & Away status** has a check mark.
3. Set **Nudge when idle for** to **Auto**.
[win] 4. Try **Settings > Activity method > Both**.
[mac] 4. Turn on **Settings > Also nudge the mouse** and allow it in Accessibility.

## Teams or Slack still show me as Away
[win] Set **Activity method** to **Mouse wiggle** or **Both**. Chat apps notice keyboard and mouse activity.
[mac] Turn on **Settings > Also nudge the mouse (Teams/Slack)** and allow it in **System Settings > Privacy & Security > Accessibility**.

## I can't find the cup
[win] Click the **^** arrow at the right end of the taskbar. To keep the cup visible, drag it onto the taskbar. If it isn't there at all, open **Kapeyn** from the Start menu.
[mac] The menu bar may be full. Quit a few menu bar apps, or hold **Command** and drag the cup to the left. If it isn't there at all, open **Kapeyn** from your Applications folder.

## Kapeyn stays awake when I didn't ask it to
Hover over the cup: it says why, for example "on a call" or "downloading". Turn off that mode under **Keep awake automatically**, or click the cup to turn Kapeyn off until it ends.

## Kapeyn turned itself off
- A **Keep awake for** timer ran out, or
- your **work hours** ended, or
- it paused because the **battery is low** (the cup shows why). Click the cup to keep it on anyway.

## The kapeyn command isn't found
[win] Open a **new** terminal window after installing. If it's still missing, run the one-line install again (see **Command line**).
[mac] Choose **Command line...** in the menu and click **Install command**, then open a new Terminal window.

[mac] ## My MacBook still sleeps when I close the lid
[mac] That's a macOS rule no app can change, unless the Mac is connected to power and an external display.

# About
**Kapeyn** {version}

Keeps your {computer} awake: smart, light and private.

- Website and downloads: github.com/jvbalcita/kapeyn
- Made for Windows 10/11 and macOS 12 or later.
- No administrator rights, no internet access, no tracking.
- Free and open source under the MIT License.
