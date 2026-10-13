# Vex

A small desktop companion for Windows. He lives on your taskbar in a dark red hoodie, keeps out of the way of whatever you're working on, reminds you about breaks and meetings, keeps your to-dos, and chats when you click him. Dry sense of humour; means well.

## What he does

- **Wanders the taskbar** on any monitor, gets out of the way of the window you're using, and sits, reads or checks his phone when he's bored. Drag him around; drop him on top of a window and he'll stand on it.
- **Nudges** you about eye breaks, stretching, water, lunch and long stretches on one thing. At most one every 15 minutes, never while you're in a call. The stretch nudge waits while TeamViewer or remote desktop is in front, until you switch window (30 minutes at most). He knocks on the screen and waits until you answer.
- **Lunch:** when you head off (the lunch nudge's "Going", or right-click → Lunch break) he sits in a corner, eats a sandwich from his lunch box, then naps for the rest of the break (30 minutes by default).
- **Meeting reminders** from classic Outlook (if it's running), with a "Prep me" button. A short **morning brief** when you first sit down.
- **Going home:** tell him when you leave (Setup) and he says when to pack up, with the drive home. Live traffic needs a free TomTom key; without one you get a Google Maps button.
- **Learns your work:** every night he writes a short diary of the day and files what he picked up (projects, people, kit, terms) on his own pages. Now and then he asks about something he keeps seeing ("What's ChromSpace_2?"). You can let him read the text of windows from apps you choose.
- **Claude Code alerts:** tells you when a Claude Code session needs you or has finished (optional).
- **Chat:** click him. He knows roughly what you've been doing today, your meetings and your to-dos, and can add to-dos, tick them off, take notes, set reminders and remember things about you.
- **The mug** empties over time. Click it when you refill yours. He counts.

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
| Right-click Vex | Menu: nap, hide, other screen, forget the chat… |
| Tray icon | Setup…, Appearance (light/dark, hoodie colour), start with Windows, what Vex knows, show/hide, quit |
| Typing in the chat | Enter sends; Shift+Enter or Shift+Space starts a new line |
| Ctrl+V in the chat | Paste a picture (screenshot, diagram, error message) to send with your message; up to 3, ✕ on a thumbnail removes it |
| Chat header | Copy the chat as text (Ctrl+Shift+C) or as a picture |
| Pending (chat header) | Nudges and questions you didn't answer, with their original buttons, so you can pick one later. The badge shows how many are waiting; they're cleared after 7 days |

**Notes and to-dos** go in a folder of plain markdown files (default `Documents\Vex Notes`), or the Lab Notebook if you have it:

- `Journal\YYYY-MM-DD.md`: each day's log. To-dos are `- [ ] item`; Vex ticks them `- [x]` in place, so you keep the history.

Vex's own pages (*About you*, *Remembered*, *Diary*, *Projects*, ...) are markdown files in `%LOCALAPPDATA%\Vex\memory` (tray → *Memory*). Edit them freely; he reads them back. Each day's chat is kept in `%LOCALAPPDATA%\Vex\chats` (tray → *Chat history*); each morning starts a fresh conversation.

## Privacy

- Everything Vex keeps stays on your PC: `%LOCALAPPDATA%\Vex` (settings, chat history, activity log) and your notes folder.
- The optional activity log records which app and window title is in front. It never records password managers, private browsing, or windows with words like "password", "sign in" or "bank" in the title (editable in `config.json`). Turn it off in Setup.
- When you chat, your message goes to Claude through your own Claude Code login, along with a short summary of your day (time at the desk, recent apps, window titles if the log is on, meetings, to-dos and his notes), plus the text of the window in front only for apps you've listed, and any pictures you paste into the message (only those; he never takes screenshots). The nightly diary and his occasional questions send the same kind of summary.
- If you set a TomTom key, your work and home addresses go to TomTom to work out the drive home.
- Vex and his bubbles never appear in screenshots or screen shares.

## Settings

Tray → **Setup…** for the main choices. Everything else (timings, nudge intervals, exclusion lists, knock sound) is in `%LOCALAPPDATA%\Vex\config.json`; tray → *Settings (config.json)* opens it.

## Removing him

1. Tray → Setup…: untick *Start me with Windows* and *Tell me when Claude Code needs you*, then **Quit**.
2. Delete `Vex.exe` and `%LOCALAPPDATA%\Vex`. Your notes folder is yours to keep.

## Building from source

Needs the .NET 10 SDK. Run `powershell -ExecutionPolicy Bypass -File .\build.ps1`; it produces a self-contained `Vex.exe` in the repo root. Design notes are in [SPEC.md](SPEC.md).
