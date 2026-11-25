# Kapeyn roadmap

Ideas we want to build, but that still have open questions. Nothing here is promised or scheduled. When an item is built, it moves to the release notes and comes off this list.

Have an idea or an answer to one of the questions? [Open an issue](https://github.com/jvbalcita/kapeyn/issues).

| Idea | Status |
|---|---|
| [Presence-aware mode](#presence-aware-mode) | Needs research: connection and reliability |
| [Update notices](#update-notices) | Ready to design |
| [Translations](#translations) | Ready to design |
| [Pomodoro / focus timer](#pomodoro--focus-timer) | Under consideration |
| [Ideas we decided against](#ideas-we-decided-against) | Not planned |

---

## Presence-aware mode

**In one sentence:** while your phone is near your computer, Kapeyn keeps it awake; when you walk away with your phone, Kapeyn locks the computer.

**Why:** every keep-awake app today keeps the computer awake and unlocked whether you're at your desk or not. That's a security risk (an unlocked laptop in an office or café), and it shows you as available in Teams or Slack while you're actually away. Presence mode would keep the computer awake only while you're really there, and lock it as soon as you leave.

**How it would work**
- Uses the Bluetooth signal of a phone you've already paired with the computer. Nothing to install on the phone, and nothing goes over the internet.
- Strong signal: you're nearby, so the computer stays awake.
- Weak or no signal for about 30 seconds: Kapeyn locks the computer and stops keeping it awake.
- Kapeyn never unlocks the computer. You unlock it as usual (password, PIN, fingerprint).
- If you walk away during a download, Kapeyn locks the screen but doesn't let the computer sleep, so the download keeps going.
- Off by default. Turned on in Settings, where you pick your phone and how far counts as "away" (desk, room, further).

**Open questions (these must be answered before building it)**

*Connection*
- Does the phone have to be actively connected, or is being paired and in range enough? Phones often drop idle Bluetooth connections to save battery. Can Kapeyn read the signal without keeping a connection open?
- What happens when the phone is paired but not connected? Should Kapeyn reconnect on its own, and how often?
- iPhone with Windows: does Windows keep a readable connection to an iPhone? Windows' own Dynamic Lock is known to be unreliable with iPhones.
- Can a smartwatch or earbuds be used instead of a phone?
- Does it work with the Bluetooth adapters in most work laptops, and through USB Bluetooth dongles?

*Reliability*
- Bluetooth signal strength jumps around (pocket, bag, body in the way, walls). How much smoothing is needed so the computer doesn't lock while you're sitting still, without making locking slow when you really leave?
- How do we pick a good "away" distance for each phone and laptop pair? Is a calibration step needed ("sit at your desk for 10 seconds, then walk away")?
- What if the phone's battery runs low or it enters battery saver and Bluetooth goes quiet?
- What if the phone dies, Bluetooth is turned off on either side, or the computer's Bluetooth is off or missing?
  - Proposed answer: treat "can't tell" as "away" and lock (the safe choice), but tell the user why once, so they don't think Kapeyn is broken.
- What if you leave the phone on the desk and walk away? Kapeyn would think you're still there. Is that acceptable if the Help explains it? (Windows Dynamic Lock has the same limit.)
- How should it work together with the other triggers? For example: on a call with your phone left in another room, should Kapeyn lock?

*Privacy and permissions*
- macOS asks for Bluetooth permission. How do we explain that clearly the first time?
- Kapeyn should only read the signal of the one device the user picked, never scan for or keep a list of other devices nearby.

*Testing*
- Needs testing with several Android phones and iPhones, on both Windows and Mac, before release.

---

## Update notices

**In one sentence:** Kapeyn tells you when a new version is out.

- A small "Kapeyn 2.4 is available" item in the menu, and one click to update (re-runs the installer, or shows the `brew upgrade` / `winget upgrade` command if you installed that way).
- Checks GitHub's releases page at most once a day. Sends nothing about you.
- Can be turned off in Settings.

**Open questions:** how to detect which way Kapeyn was installed (installer, Homebrew, winget, manual download) so the update button does the right thing.

---

## Translations

**In one sentence:** Kapeyn's menus and Help in more languages.

- The Help guide is already one shared file (`help/help.md`), so each language is mostly a translated copy.
- Menus and messages would move into a per-language strings file.
- Kapeyn follows the computer's language, with English as the fallback.

**Open questions:** which languages first, and who reviews each translation.

---

## Pomodoro / focus timer

**In one sentence:** work in focused blocks (for example 25 minutes) with short breaks in between, and Kapeyn keeps the computer awake during the focus blocks.

**Why it could fit:** Kapeyn already has timers, a menu and notifications, so a focus timer is a small step. During a focus block the computer stays awake; when the block ends Kapeyn tells you to take a break.

**Why we're not sure yet:**
- Many good Pomodoro apps already exist, and they're built into some tools people already use. Kapeyn's goal is "keep the computer awake, intelligently", and a full Pomodoro app (stats, task lists, long-break rules) would pull it away from that.
- A small version might be enough: "Focus for 25 minutes" in the *Keep awake for* menu, with a "take a break" notification at the end.

**Open questions:**
- Is the small version enough, or do people want cycles (focus, break, focus, break, long break)?
- Should a break lock the screen to make you step away, or just notify?
- Should Teams and Slack show "Do not disturb" during focus? (That needs their apps' cooperation and may not be possible.)

---

## Ideas we decided against

- **Controlling Kapeyn from your phone over the network.** It would mean opening a network port on your computer. That's a real security risk for little benefit.
- **More ways to keep chat apps green while you're away.** This is the part of keep-awake apps most likely to get people in trouble at work. Presence-aware mode is the honest answer to it.
