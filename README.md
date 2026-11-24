# Kapeyn ☕

Kapeyn keeps your computer awake. It's a tiny tray / menu bar app for **Windows** and **macOS**. No admin rights are needed on either.

- **Click the cup** to turn it on or off. A steaming cup means awake; an outline means off.
- **Right-click** (on a Mac, also Control-click) for options:
  - **Keep awake for**: indefinitely, or 15 min up to 8 hours.
  - **Keep screen on**
  - **Prevent screen lock & Away status**
  - **Nudge when idle for**: 30 s, 1 min, 2 min or 4 min.
  - **Start with Windows** / **Start at login**
- Only one copy ever runs. Launching it again turns the running copy on and tells you it's already running.

---

## Windows

**Install** (copies it to `%LOCALAPPDATA%\Programs\Kapeyn`, adds a Start menu shortcut, starts it at sign-in, and starts it now):

```powershell
powershell -ExecutionPolicy Bypass -File .\install.ps1
```

Uninstall: `powershell -ExecutionPolicy Bypass -File .\install.ps1 -Uninstall`. You can also just double-click `dist\Kapeyn.exe`.

On Windows 11 the icon may be hidden behind the **^** on the taskbar. Drag it onto the taskbar to keep it visible.

**Activity method** (Windows only) lets you choose how Kapeyn nudges: mouse wiggle (the pointer is put back exactly), F15 key (Caffeine's method), or both.

**Build:** `powershell -ExecutionPolicy Bypass -File .\build.ps1` uses the C# compiler built into Windows and writes `dist\Kapeyn.exe`. The code is in `src\Kapeyn.cs`.

**Self-check:** `dist\Kapeyn.exe --nudge-test` exits with the number of input events Windows accepted (it should be 4).

---

## macOS

1. Copy `dist/Kapeyn-mac.zip` to the Mac and unzip it. You can also copy the `mac` folder directly.
2. Open **Terminal** in the unzipped `mac` folder and run:

   ```bash
   bash install.sh
   ```

   If it says the Swift compiler is missing, run `xcode-select --install`. This installs Apple's free Command Line Tools in about 5 minutes. Then run `bash install.sh` again.

The installer builds `Kapeyn.app` on your Mac, copies it to `~/Applications`, sets it to start at login, and starts it. Because it's built on your own Mac, there's no "unidentified developer" warning. macOS may show a "Background item added" notice for the login item; that's expected.

Uninstall: `bash install.sh --uninstall`

**How it works on a Mac**

- A power assertion stops idle sleep and display sleep. To check it, run `pmset -g assertions | grep Kapeyn`.
- When you're idle, Kapeyn tells macOS "the user is active". That resets the timer behind the screen saver and screen lock, and needs no permissions.
- **Also nudge the mouse (Teams/Slack)** is off by default. Turn it on if Teams or Slack still shows you as Away. macOS will ask for **Accessibility** permission (System Settings → Privacy & Security → Accessibility → Kapeyn). If you rebuild or reinstall Kapeyn, you may need to turn that switch off and on again.
- A MacBook still sleeps when you close the lid, unless it's connected to an external display and power.

If the cup doesn't appear (on Macs with a notch, the menu bar can run out of room), close a few menu bar apps or hold ⌘ and drag the cup to the left.

---

## Why this works when Caffeine doesn't

Kapeyn has two layers, and both are on by default:

1. **A power request.** The OS won't sleep or turn off the display while it is active.
2. **Activity simulation, only while you're idle.** Company-managed computers often lock after a period of *no input*, whatever power requests say. Caffeine relies on an F15 key press that some setups ignore, so running more copies didn't help. Kapeyn uses input the OS always counts.

Settings are stored in `%APPDATA%\Kapeyn\settings.ini` on Windows, and in `defaults read com.kapeyn.Kapeyn` on macOS.
