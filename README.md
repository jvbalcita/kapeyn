# Kapeyn ☕

Kapeyn keeps your computer awake, and is smart about it. It's a tiny tray / menu bar app for **Windows** and **macOS** that needs no admin rights.

- **Click the cup** to turn it on or off. A steaming cup means awake; an outline means off. Or press **Ctrl+Alt+K** (Windows) / **⌃⌥K** (Mac) anywhere.
- **Right-click** (on a Mac, also Control-click) for everything else. On Windows, the menu stays open while you flip settings, so you can change several in a row.
- Hover over the cup, or open the menu, to see *why* it's awake ("on a call", "running npm", "work hours") and when it last nudged.

## What it does

| | |
|---|---|
| **Keeps the PC/Mac awake** | A power request stops sleep and screen-off. |
| **Prevents screen lock & "Away"** | When you're idle, Kapeyn sends a tiny invisible input. That resets the idle timer behind company screen-lock policies and Teams/Slack "Away". |
| **Lock doctor** | *Lock doctor: what locks my PC?* finds every timer that can lock, blank or sleep your machine, including ones set by your company. It checks each one is covered, runs a self-test, and gives a verdict. |
| **Auto timing** | Nudge timing defaults to **Auto**: half of the shortest lock timer the doctor finds (between 30 s and 4 min), so it's never too late. |
| **Keep awake automatically** | Stays awake while you're **on a call** (microphone in use), while **downloading or streaming**, while **chosen apps are open**, or during **work hours**. *Set work hours…* takes any start and end time, including overnight shifts like 10 PM – 6 AM. When work hours end, Kapeyn can turn itself off, and optionally **lock** or **shut down** the computer (both off by default). Shutdown gives you 60 seconds to cancel, and apps with unsaved work can still stop it. |
| **`kapeyn` command** | `kapeyn on 2h`, `kapeyn off`, `kapeyn status`, `kapeyn doctor`, and `kapeyn run npm run build`, which keeps the machine awake exactly as long as the command runs. |
| **Knows when to stop** | Lets the screen turn off when *you* lock the machine. Pauses on low battery (click the cup to override). If you turn it off during a call or download, it stays off until that ends. |
| **One copy only** | Launching it again just turns the running copy on. |

## Download

Get the latest from the [Releases page](https://github.com/jvbalcita/kapeyn/releases/latest):

- **`Kapeyn.exe`** (Windows): double-click and go.
- **`Kapeyn-Windows.zip`** (Windows): the app, the `kapeyn` command and an installer.
- **`Kapeyn-macOS.zip`** (Mac): the app (Apple silicon & Intel).

Both are built automatically from this source code by GitHub Actions (`.github/workflows/release.yml`). To publish a new version, push a tag, e.g. `git tag v2.0.1` then `git push origin v2.0.1`.

---

## Windows

**Install** (copies the app and the `kapeyn` command to `%LOCALAPPDATA%\Programs\Kapeyn`, adds the command to your PATH, adds a Start menu shortcut, starts Kapeyn at sign-in, and starts it now):

```powershell
powershell -ExecutionPolicy Bypass -File .\install.ps1
```

Run this from the unzipped `Kapeyn-Windows.zip`, or from this repository (it builds first). To uninstall: `powershell -ExecutionPolicy Bypass -File .\install.ps1 -Uninstall`.

- On Windows 11 the cup may be hidden behind the **^** on the taskbar. Drag it onto the taskbar to keep it visible.
- **Settings → Activity method** chooses how Kapeyn nudges: mouse wiggle (the pointer is put back exactly), F15 key (Caffeine's method), or both.
- **Build:** `powershell -ExecutionPolicy Bypass -File .\build.ps1` uses the C# compiler built into Windows and writes `dist\Kapeyn.exe` (the app) and `dist\kapeyn.com` (the command). Windows runs `.com` before `.exe`, so typing `kapeyn` gets the command.

## macOS

The quickest option is the download from the Releases page. If you'd rather avoid Apple's "unverified app" prompt, build it on your Mac instead:

1. Download this repository (Code → Download ZIP, or `git clone`).
2. Open **Terminal** in its `mac` folder and run:

   ```bash
   bash install.sh
   ```

   If it says the Swift compiler is missing, run `xcode-select --install` (Apple's free Command Line Tools, about 5 minutes). Then run it again.

This builds `Kapeyn.app`, copies it to `~/Applications`, sets it to start at login, and starts it. To uninstall: `bash install.sh --uninstall`.

- **The `kapeyn` command:** the installer adds it when it can. Otherwise choose **Command line… → Install command** in Kapeyn's menu, which asks for your password once.
- **Also nudge the mouse (Teams/Slack)** is off by default. With *Keep screen on*, Kapeyn's power assertion already stops the screen saver and lock. Chat apps watch real keyboard/mouse input, though, so turn the mouse nudge on if they still show you as Away; macOS will ask for **Accessibility** permission. The lock doctor's self-test tells you whether you need it.
- Call detection is based on "a microphone is in use". A headset that combines speakers and mic may also count while you're just playing music.
- A MacBook still sleeps when you close the lid, unless it's connected to power and an external display.

---

## Why this works when Caffeine doesn't

Company-managed computers often lock after a period of **no keyboard/mouse input**, whatever power requests say. On the author's PC the lock doctor found a 5-minute company lock. Caffeine relies on an F15 key press that some setups ignore, and PowerToys Awake and most Mac apps only send power requests. Kapeyn combines a power request with idle-only input that the OS always counts. It picks the timing from your machine's actual lock settings, then tells you whether everything is covered.

Settings are stored in `%APPDATA%\Kapeyn\settings.ini` on Windows, and in `defaults read com.kapeyn.Kapeyn` on macOS.
