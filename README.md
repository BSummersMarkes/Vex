# Vex

A small desktop companion for Windows. He lives on your taskbar in a deep teal hoodie, keeps out of the way of whatever you're working on, reminds you about breaks and meetings, keeps your to-dos, and chats when you click him. Dry sense of humour; means well.

## What he does

- **Wanders the taskbar** on any monitor, gets out of the way of the window you're using, and sits, reads, checks his phone, juggles or climbs up the side of a window onto its top (and back down later) when he's bored (Setup → Behaviour → Idle actions turns each one on or off). Drag him around; drop him on top of a window and he'll stand on it. Click him mid-climb and he comes straight down; move or close the window he's on and he drops off, unimpressed.
- **Nudges** you about eye breaks, stretching, water, lunch and long stretches on one thing. At most one every 15 minutes, never while you're in a call. The stretch nudge waits while TeamViewer or remote desktop is in front, until you switch window (30 minutes at most). He knocks on the screen and waits until you answer.
- **Breaks are never assumed:** being away from your desk doesn't count (you might be in the lab or a meeting). Tell him on the stretch nudge ("Taking a break" / "Had my break"), the lunch nudge ("Going" / "Had lunch"), or in the chat ("had lunch at 12:30"), and the sitting timer restarts. "Not now" puts the nudge off; unanswered ones wait in the chat's Pending tab.
- **Lunch:** when you head off (the lunch nudge's "Going", telling him in the chat, or Setup → Behaviour → Lunch break) a lunch timer starts and he has his own: sits in a corner with his mug beside him, eats a sandwich for 5–10 minutes, then naps for the rest (30 minutes by default). When time's up he wakes with a start and says so; come back early and he wakes up, groggily. While he naps he only pipes up for important things (meetings, reminders, Claude Code). On/off and the length are in Setup → Behaviour.
- **Meeting reminders** from classic Outlook (if it's running), with a "Prep me" button. A short **morning brief** when you first sit down.
- **Going home:** tell him when you leave (with a different time for any weekday under *Except on*, e.g. earlier on Fridays) and how you get home (car, train or bus, bike, on foot) in Setup, and he says when to pack up, with a Google Maps button for the way home (by train or bus it lists the next departures). Driving, a free TomTom key adds the live traffic. If he says he's only placed an address roughly, give a fuller address or postcode in Setup.
- **Home days:** in Setup → Home days, press *This network is home* at home and *This network is the office* at work (save as many as you like: wired, Wi-Fi, with the VPN on). He tells them apart from what the PC can see locally (Wi-Fi name, router address, DNS suffix, VPN); nothing is sent anywhere. On a home day he nudges you to clock in when you first sit down (up to three times, until you press Done) and to clock out at your leave time instead of the pack-up nudge. Guessed wrong? Right-click him → Where today, or just tell him in chat.
- **Learns your work:** every night he writes a short diary of the day and files what he picked up (projects, people, kit, terms) on his own pages. Now and then he asks about something he keeps seeing ("What's ChromSpace_2?"). You can let him read the text of windows from apps you choose.
- **Claude Code alerts:** tells you when a Claude Code session needs you or has finished (optional). If you like, he also sits in the bottom-right corner typing on a little laptop while a session is working, and nods when it's done (Setup → Behaviour).
- **Chat:** click him. He knows roughly what you've been doing today, your meetings and your to-dos, and can add to-dos, tick them off, take notes, set reminders and remember things about you. Reminders can repeat: "remind me to take my tablets every day at 10", "weekdays at 2 to drink some water", "Mondays and Thursdays at 9:30 …". A repeating one has a **Stop these** button, or ask him to cancel it. Plain timers and reminders are set on the spot without asking Claude, so they work offline too: "timer 10m", "set a timer for 25 minutes for the tea", "remind me in 20m to call Sam", "remind me to call Sam at 3pm". Anything less clear-cut goes to Claude as usual (`ChatLocalReminders` in config.json turns this off).
- **Dresses up** for the season: a Santa hat for Christmas, bunny ears for Easter and an oni mask on the side of his hood for Halloween, from five days before until the day itself. Setup → Theme turns it off, and *Try one on* puts any of them on him straight away to see how it looks.
- **The mug** empties over time. Click it when you refill yours. He counts. Clicked it by mistake? Right-click the mug → Undo last refill.

## Getting started

1. Copy `Vex.exe` somewhere permanent, e.g. `Documents\Vex\Vex.exe`. It's a single file; nothing to install.
2. Run it. Windows may say "Windows protected your PC" because the file isn't signed: **More info → Run anyway**.
3. Answer the few setup questions (your name, where to keep notes, a couple of options).

For chat you need **[Claude Code](https://claude.com/claude-code) installed and signed in** with your own account. Everything else works without it. Please check that using Claude this way is OK where you work.

## Using him

| | |
|---|---|
| Click Vex | Open the chat (click him again, or anywhere else, to close it) |
| Esc, ✕ or click away | Close the chat. Click-away closing can be turned off in Setup → Behaviour; it stays open while he's replying or waiting for a Build it click. A half-written message is kept for next time |
| Ctrl+Alt+V | Open the chat from anywhere (`QuickChatHotkey` in config.json) |
| Drag Vex | Pick him up and drop him (on the taskbar or on a window) |
| Click the mug | "I've refilled my coffee" |
| Right-click the mug | Today's coffee count and Undo last refill (count down one, mug back to its level before; repeat to step back further) |
| Right-click Vex (not the mug) or the tray icon | Setup…, What Vex knows…, Where today (working from home / at the office today, or work it out from the network), Theme (light/dark, hoodie colour; Original brings back the dark red), Quit. Left-click the tray icon to chat |
| Setup → Theme | Hoodie lightness: slide it darker or lighter and watch him change; Save keeps it. Seasonal outfits on or off, and Try one on |
| Setup → Behaviour → Right now | Lunch break / End lunch, nap / wake up, I refilled my coffee, other screen, hide / show, chat, forget today's chat |
| Setup → Privacy → Your data | Open his memory pages, chat history, config.json or the data folder |
| Typing in the chat | Enter sends; Shift+Enter or Shift+Space starts a new line |
| Ctrl+V in the chat | Paste a picture (screenshot, diagram, error message) to send with your message; up to 3, ✕ on a thumbnail removes it |
| Chat header | Copy the chat as text (Ctrl+Shift+C) or as a picture |
| Pending (chat header) | Nudges and questions you didn't answer, with their original buttons, so you can pick one later. The badge shows how many are waiting; they're cleared after 7 days |

**Notes and to-dos** go in a folder of plain markdown files (default `Documents\Vex Notes`), or the Lab Notebook if you have it:

- `Journal\YYYY-MM-DD.md`: each day's log. To-dos are `- [ ] item`; Vex ticks them `- [x]` in place, so you keep the history.
- With the Lab Notebook, open journal to-dos are also copied onto its To-Do list, and ticking one in either place ticks the other (`TodoListMirror` in `config.json` turns this off).

Vex's own pages (*About you*, *Remembered*, *Diary*, *Projects*, ...) are markdown files in `%LOCALAPPDATA%\Vex\memory` (Setup → Privacy → *Memory*). Edit them freely; he reads them back. Each day's chat is kept in `%LOCALAPPDATA%\Vex\chats` (Setup → Privacy → *Chat history*); each morning starts a fresh conversation.

## Privacy

- Everything Vex keeps stays on your PC: `%LOCALAPPDATA%\Vex` (settings, chat history, activity log) and your notes folder.
- The optional activity log records which app and window title is in front. It never records password managers, private browsing, or windows with words like "password", "sign in" or "bank" in the title (editable in `config.json`). Turn it off in Setup.
- When you chat, your message goes to Claude through your own Claude Code login, along with a short summary of your day (time at the desk, recent apps, window titles if the log is on, meetings, to-dos and his notes), plus the text of the window in front only for apps you've listed, and any pictures you paste into the message (only those; he never takes screenshots). The nightly diary and his occasional questions send the same kind of summary.
- If you set a TomTom key and drive, your work and home addresses go to TomTom to work out the drive home.
- Vex and his bubbles never appear in screenshots or screen shares.

## Settings

Tray or right-click Vex → **Setup…** for the main choices. Everything else (timings, nudge intervals, exclusion lists, knock sound) is in `%LOCALAPPDATA%\Vex\config.json`; Setup → Privacy → *Settings (config.json)* opens it.

## Removing him

1. Tray → Setup…: untick *Start me with Windows* and *Tell me when Claude Code needs you*, then **Quit**.
2. Delete `Vex.exe` and `%LOCALAPPDATA%\Vex`. Your notes folder is yours to keep.

## Vex Lite (for sharing)

A cut-down Vex for people without Claude: the character, nudges, lunch, the mug, going home, meetings from classic Outlook and **reminders** (one-off and repeating), set from a small panel when you click him. No chat, diary, memory pages, workshop, Claude Code alerts, Lab Notebook or window reading, and nothing ever leaves the PC. It's a single ~230 KB file on .NET Framework 4.8 (part of Windows 10/11), so it can go on an email, though many work mail systems block .exe attachments. Its settings live in `%LOCALAPPDATA%\Vex Lite`, so it can run beside the full Vex. The guide for the people you share it with is [src/VexLite/README-LITE.md](src/VexLite/README-LITE.md).

Build it with `powershell -ExecutionPolicy Bypass -File .uild-lite.ps1`, which writes `Vex Lite.exe` to the repo root.

## Vex for Mac (early)

A separate, cut-down Vex for macOS in `src/VexMac`, built on Avalonia so it runs on a Mac (and on Windows, for trying it out). So far he walks the bottom of the screen above the Dock, blinks, breathes, dozes when you're idle, can be dragged and dropped, has his mug, and nudges about a stretch and water; menu-bar icon, start at login. No Claude, nothing leaves the Mac. `build-mac.ps1` makes `Vex-mac.zip` (Vex.app, about 44 MB) as Intel code, which Apple Silicon Macs run through Rosetta without Apple's code signing. Needs macOS 14 or newer. How to get it onto the Mac: [src/VexMac/README-MAC.md](src/VexMac/README-MAC.md).

## Building from source

Needs the .NET 10 SDK (Vex Lite also needs the .NET Framework 4.8 targeting pack, which the SDK fetches by itself). Run `powershell -ExecutionPolicy Bypass -File .\build.ps1`; it produces a self-contained `Vex.exe` in the repo root. Design notes are in [SPEC.md](SPEC.md).
