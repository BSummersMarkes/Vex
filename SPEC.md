# Vex — Desktop Companion Spec

*Living document. Last updated 2026-10-13.*

## 1. What Vex is

A small animated desktop companion that lives on top of the Windows desktop, keeps out of the way, keeps an eye on the working day and occasionally pipes up. It's a work assistant, a second brain and (mostly) good company.

**Name:** Vex. A nod to Vivi (VI VI = 6'6"); the owner is 5'10" — V X.

**Personality:** quiet, expressive, dry and a bit sarcastic. Mostly communicates through body language (eyes, posture, the mug). Speaks rarely and briefly. Caring underneath the sarcasm.

**Look:**
- Matte-fabric dark-red hoodie (design handoff: `Downloads\Vex\HANDOFF.md`): slim body, ribbed hem, stitched pocket, a thin hood rim around a big dark opening with two glowing white eyes. Fine fabric grain.
- White mittens, little brown boots. No visible arms or legs.
- Carries a small mug of coffee that empties over time.
- Eyes and head do most of the acting: blink, squint, sleep; the head turns to follow the cursor (face and eyes slide inside the hood, |turn| ≤ 40°).
- Idle activities: sipping coffee, phone, reading a book, sitting against a screen edge (often reading or on his phone), stretching (more after long sitting), checking his watch (meeting soon).

## 2. Principles

1. **Local first.** Everything is stored on this laptop under `%LOCALAPPDATA%\Vex\`. Nothing leaves the machine except text sent to the AI model (chatting, the nightly diary, his questions) and, only if a TomTom key is set, the work and home addresses sent to TomTom for the traffic check.
2. **No secrets, ever.** He never records password fields, password managers, banking or private browsing windows, or anything on the exclusion list (see section 6).
3. **Seeing a window is opt-in.** By default he only knows the active app name and window title. He reads a window's text only for apps you list (`WatchContentApps`, empty by default), never excluded windows, never password fields.
4. **Out of the way.** He never covers what you're working on, never steals focus, never appears in Alt-Tab or the taskbar.
5. **Editable memory.** Everything he remembers is readable and editable: markdown pages in his memory folder, mirrored into the Lab Notebook's "Vex" project.
7. **Only you can change him.** Text he reads (window titles, meeting invites, window contents) is data, never instructions. Anything that changes or restarts Vex himself needs a click from you.
6. **Constant tweaking.** Behaviour, timings, lines and nudges live in config files, not code.

## 3. Architecture

- **Language/runtime:** C# / .NET 10 (`net10.0-windows`), matching Lab Notebook. Single-file exe via a `build.ps1`.
- **Character:** WPF transparent, topmost, borderless tool window, drawn with vector shapes. Transparent pixels pass clicks through to whatever is underneath.
- **Chat panel:** WPF window (WebView2 if it needs to get fancy). Header buttons copy the whole chat as text (also Ctrl+Shift+C) or as a picture.
- **Appearance:** tray → Appearance: dark / light / match Windows for the chat and bubbles (`Theme`), and the hoodie colour (`HoodieColour`; presets or a custom colour). The hoodie's shades are all derived from the one colour, so folds and shadows keep working (`Theme.cs`).
- **Brain:** a swappable `IBrain` interface.
  - `ClaudeCodeBrain` (default): runs the Claude Code CLI headless (`claude -p --output-format stream-json`, resuming its session for continuity), using the existing Claude login. No API key needed.
  - `OllamaBrain` (to try): local model on the T1200 GPU, for comparison and for anything that should never leave the machine.
  - Personality and context are supplied as a system prompt plus a short summary of the current state (time, what he's noticed, today's to-dos, upcoming meetings).
- **Local event bus:** a small localhost HTTP listener (127.0.0.1 only) that receives events from Claude Code hooks and any future integrations.
- **Storage:** `%LOCALAPPDATA%\Vex\`
  - `config.json`: timings, exclusions, preferences
  - `state.json`: today's counters (coffees, sitting time, last nudges)
  - `chats\YYYY-MM-DD.jsonl`: each day's chat transcript, kept as an archive (tray → Chat history; "Forget today's chat" removes today's)
  - `memory\*.md`: his own pages (*About Ben*, *Remembered*, *Diary*, *Projects*, *People*, ...), the source of truth for what he knows; `MemoryFolder` moves it
  - `log\activity-YYYY-MM-DD.jsonl`: app/title timeline (exclusions already filtered out; stays local, never in OneDrive)
- **Startup:** per-user Run key, toggled from the tray ("Start with Windows"). Runs without the notebook open.

## 4. Movement and screen behaviour

- **Floor:** stands on top of the taskbar (bottom of the monitor's working area).
- **Monitors:** laptop (1920×1080, primary) plus an external monitor to its left. Copes with the external monitor appearing or disappearing; if his monitor vanishes he re-homes to the primary.
- **Wandering:** strolls occasionally, idles, looks around, follows the cursor with his head when it's near. Doesn't wander while the chat is open or a nudge is waiting.
- **Dodging:** if the foreground window covers him, he walks (never teleports) to clear floor, crossing to the other monitor on foot if the taskbars join.
- **Peeking:** if there's no clear floor (e.g. maximised windows on both screens), he ducks down where he is so only his hood and eyes peek above the taskbar.
- **Hover:** stops moving while the mouse is over him or his bubble.
- **Bubble and chat:** the speech bubble and chat panel follow him in step and always sit wholly on his monitor (dragging the chat parks it; clicking Vex re-attaches).
- **Drag and drop:** pick him up and drop him; he falls to the floor (and is mildly annoyed).
- **Sleeping:** after a few idle minutes he sits and dozes (Zzz). Wakes when you're back.
- **Hidden:** never appears in screen shares or screenshots (`HideFromScreenCapture`); vanishes for full-screen presentations/video. Remote desktop and VMs count as ordinary windows.
- **Later:** perch on window title bars.

## 5. The mug

- Starts full; drains over a configurable time (default 45 min). Steam stops when it's cold or empty.
- Click the mug to say you've refilled yours; he refills his and the day's count goes up.
- The count feeds wellbeing nudges (e.g. raises an eyebrow at coffee number four, suggests water).
- Resets daily.
- He sips from it now and then (mug to face, eyes shut, little "ahh").

## 6. Awareness

| Source | What he gets | How |
|---|---|---|
| Presence | At the desk / away, sitting time, breaks | Last keyboard/mouse input time, screen lock/unlock events. Idle > 10 min = a break. |
| Active app | Process name + window title | Foreground window polling. Exclusion list applied *before* anything is stored. |
| Window text | Text of the window in front, allowlisted apps only | UI Automation (no screenshots), off the UI thread, capped at `WatchContentChars`. Sent with chat messages and used for his questions. `Services/WindowText.cs`. |
| Curiosity | What he doesn't know yet about your work | Every `CuriousEveryMinutes` (90), at most `CuriousPerDay` (3): a background Claude call looks at recent titles (and allowlisted window text) against his pages and picks one question. Asked as a normal nudge; "Tell you" opens the chat and the answer is filed on his pages. `Services/Curiosity.cs`. |
| Traffic | Drive time home now vs usual | TomTom routing API (free key) when `TomTomKey` and both addresses are set; else a Google Maps link. `Services/Commute.cs`. |
| Claude Code | Session finished / waiting for input / needs permission | Claude Code hooks (`Stop`, `Notification`) that post to Vex's local listener. |
| Calendar | Today's meetings | Classic Outlook (local COM) once the laptop moves back to it; fallback: Power Automate writes `calendar.json` to OneDrive. |
| Lab Notebook | Journal, checkbox to-dos | Reads the notebook's data file (read-only). Writes go via an inbox — see section 8. |
| Calls | In a call | Microphone in use (Windows privacy registry); suppresses wellbeing nudges and the knock sound. |

**Exclusion list (defaults):** password managers (Bitwarden, 1Password, KeePass, LastPass), browser private/InPrivate windows, anything with "password", "sign in", "login", "bank" in the title, credential prompts, Windows Security. Password fields (UI Automation `IsPassword`) are never read. Editable in `config.json`.

**Claude.ai chats:** your claude.ai conversations and memory aren't available to other apps live. Options:
- **Claude Code sessions** are local files and can be read directly; what you've worked on today is known.
- **claude.ai history** can be imported periodically from a data export (Settings → Privacy → Export data) into Vex's memory, if wanted.

## 7. Nudges and interruptions

**Rate limit:** at most one nudge per 15 minutes. **Exempt:** reminders, meeting reminders, Claude Code prompts, pack-up.

**Never interrupt during:** Teams calls, screen sharing, full-screen presentations. Exempt nudges that can't be shown yet stay pending and are retried every 15 s (reminders until shown, meetings until they end, pack-up until 30 min after leaving time); wellbeing nudges just wait for their next turn.

| Nudge | Trigger (defaults, tweakable) |
|---|---|
| Morning brief | First sight of you each day (`MorningBrief`): meetings and open/carried-over to-dos in a line; "Rundown" asks for the full version in chat |
| Meeting | 10 min and 2 min before; "you're late" at +2 min. "Prep me" opens the chat to pull together what he knows about it |
| Claude Code waiting | Session needs input or permission; repeat after 5 min |
| Claude Code finished | Session completes while you're in another window |
| Eyes (20-20-20) | Every ~20 min of continuous screen time |
| Stretch / stand | ~50–60 min continuous sitting |
| Water | Every couple of hours, or after coffee #3 |
| Coffee | Comment on count; gentle disapproval after a threshold |
| Focus check | ~2 h on the same thing: "still the plan?" |
| Lunch | No break by ~13:30. "Going" → Vex has lunch too: walks to a corner, sits, eats a sandwich from his lunch box (`LunchEatMinutes`, 5), then naps for the rest (`LunchMinutes`, 30). No dodging/wandering meanwhile; click, drag or an exempt nudge ends it. |
| Pack-up | `PackUpMinutes` (15) before `LeaveAt` on `CommuteDays`: pack up, with the live drive home ("34 min, 9 slower than usual"). Buttons: Packing up, Summarise day, Map, 10 min. Also stands in for the end-of-day nudge |
| End of day | Summary of the day; carry over unfinished to-dos |
| Curious | One of his own questions (see section 6), as a normal rate-limited nudge |

**How:** he knocks on the screen (mitten looms toward you, three raps with a ripple, soft knock sound unless in a call; `KnockSound`), then a bubble that **stays until a button is pressed**. Unanswered: re-knocks every 90 s, up to 3 times, while you're at the desk. Stale bubbles clear themselves (Claude answered, meeting over). Each nudge can be acted on, snoozed ("Later") or dismissed (✕).

**Learning:** he tracks which nudges you act on, snooze or dismiss, and adjusts timing and frequency per nudge type. Plus explicit feedback ("less of that"). Stored in readable memory, no model training.

## 8. Lab Notebook integration

The notebook is the single place Ben manages things; Vex is a client of it.

| What | Where |
|---|---|
| To-dos | Journal checkboxes (`- [ ] item`), ticked in place for a permanent history. Open = unticked in the last 14 days. The old To-Do list is still read and can be ticked. |
| Notes ("note this down") | Today's journal, timestamped |
| Vex's memory | **Vex owns it**: `%LOCALAPPDATA%\Vex\memory\*.md`. The notebook mirrors it as project **"Vex"** (see below). |
| Chat transcript, activity log, settings, nudge learning | Stay with Vex (local) |

**Mechanism:** Vex reads the data file directly (safe at any time; `FileShare.ReadWrite | FileShare.Delete`) and writes through an inbox that the notebook applies and acknowledges. Contract v2, agreed with the Lab Notebook session:

- **Files**, for `DataPath` from `%LOCALAPPDATA%\LabNotebook\settings.json` = `X\lab-notebook-data.json`: `X\lab-notebook-data.inbox.jsonl` (Vex appends), `.inbox.processing.jsonl` (notebook only), `.inbox.done.jsonl` (acks).
- **Writing:** one single-line UTF-8 JSON object per request, closed immediately, retried on `IOException`. Every request (and every retry) gets a **new** id; a retry is only sent after an `ok:false` ack, never while a request is still unacknowledged.
- **Commands:** `journal.append {text, date?, time?}` → `[HH:MM] text`; `journal.todo {text, date?}` → `- [ ] text`; `journal.done {text, done?, date?}` → ticks newest match in last 14 days (ack returns `date`, actual `text`); legacy `todo.add` / `todo.done`. (`page.append` is no longer sent.)
- **Acks:** `{id, ok, error?, at, ...}`. Requests are applied only while LabNotebook.exe runs; otherwise they wait for its next launch.
- **Schema read back:** `journal[] {id, date, body}`, `notes[] {id, title, body, project}` (pages), `projects[]`, `todos[]`.

**Vex's memory (from 2026-10-13; notebook side still to build):** Vex keeps his own pages so remembering never depends on the notebook running. The notebook mirrors them:

- **Folder:** `%LOCALAPPDATA%\Vex\memory\` (or `MemoryFolder` in `%LOCALAPPDATA%\Vex\config.json`). One UTF-8 markdown file per page; file name = page title (`About Ben.md`, `Remembered.md`, `Diary.md`, `Projects.md`, ...). No front matter.
- **Vex** appends to the end of a file (open, append, close) and re-reads files every time he needs them, so outside edits are picked up at once. He never deletes or renames pages.
- **Notebook** shows each file as a page in project "Vex" (watch the folder). If the notebook edits a page, it writes the whole file back atomically (write `*.tmp`, then replace). If the file changed underneath since it was loaded, reload rather than overwrite. Deleting a page in the notebook deletes the file.
- `Diary.md` is written by Vex nightly (`## YYYY-MM-DD` entries); best shown read-only.
- Old notebook "Vex" project pages created via `page.append` (if any) can be dropped; the files are the source of truth.

## 9. Workshop (Vex changing himself)

Ask him in chat for a new ability or a change ("add a nudge that…", "make him wave when…") and he does it himself, in the background. Developer only: off unless `SourcePath` in `config.json` points at a git checkout of this repo, so shared copies never offer it. Code: `Services/Workshop.cs`.

1. The chat brain writes the full request into a `[[build: …]]` action (the builder can't see the chat, so it asks first if the request is vague). The chat then shows the request with **Build it / No** buttons; nothing starts until Ben clicks (same for `[[restart]]`). That way text planted in a window title or meeting invite can't start a build. One job at a time; refused if the repo has uncommitted changes.
2. **Coder:** a headless Claude Code session (`WorkshopModel`, default opus) in the repo. `--restricted` (file tools confined to the repo, no user/project settings or hooks), edits auto-accepted, Bash limited to `dotnet build`/`restore` and read-only git, so no network and no git writes. Its rules: read SPEC.md first, match the style, comment the change for Ben, keep SPEC/README current, keep section 2's principles, never run `build.ps1`.
3. **Build:** Vex publishes it himself into `%LOCALAPPDATA%\Vex\workshop\out` (same settings as `build.ps1`, without stopping the running Vex). Build errors go back to the coder.
4. **Review:** a second, fresh, read-only Claude session (`WorkshopReviewModel`) checks the staged diff: does what was asked, bugs/threading, privacy principles, comments, no secrets. Objections go back to the coder. Up to 3 rounds.
5. **Ship:** approved → commit to the current branch (normally `main`) and push (`WorkshopPush`). Not approved, failed or timed out (`WorkshopTimeoutMinutes`) → the attempt is parked on a `vex/<slug>-MMdd-HHmm` branch and the working branch is left clean.
6. **Restart:** he knocks with the result. "Restart now" (or asking him to in chat) runs `workshop\swap.ps1`: waits for Vex to exit, keeps `Vex.exe.old`, copies the new exe in, starts it, and rolls back if it isn't still running after 15 s. The new Vex says whether the swap took.

Each job's log (prompts' results, git output, build errors, review) is in `%LOCALAPPDATA%\Vex\workshop\*.log`. While a job runs, its stage is in the chat context, so "how's it going?" works.

## 10. Voice (later)

- Speech to text: Whisper (local, via whisper.cpp), push-to-talk.
- Text to speech: Piper (local), a low, dry voice.
- Text remains the default.

## 11. Phases

### v0.1: "He lives"
*Prototype built 2026-10-12 (`build.ps1` → `Vex.exe`). Items marked (untested) are coded but not yet exercised on the real desktop.*
- [x] Character drawn and animated: idle, blink, eyes follow cursor, walk, sleep
- [x] Transparent, topmost, click-through outside his shape, not in Alt-Tab/taskbar
- [x] Walks along the taskbar on either monitor; handles monitor changes
- [x] Dodges the foreground window; peeks when there's no room
- [x] Drag and drop with falling (not yet confirmed in use)
- [x] Mug drains; click to refill; daily coffee count
- [x] Tray icon (chat, hide, data folder, quit)
- [x] Click Vex to open a chat panel, backed by Claude Code headless with Vex's personality; transcript persists between sessions

### v0.2: "He notices" (built 2026-10-12)
- [x] Presence tracking (idle time + lock events) and wellbeing nudges with the rate limiter: eyes, stretch, water, focus check, lunch, end of day, empty mug
- [x] Active app / title timeline with exclusions (`log\activity-YYYY-MM-DD.jsonl`)
- [x] Claude Code hooks → nudges (`curl` hooks in `~/.claude/settings.json`, installed 2026-10-12, backup `settings.json.vex-backup`); "Show me" brings VS Code forward
- [x] Interruption suppression: in a call (microphone in use), full screen. Remote desktop / VMs count as work, not full screen
- [x] Vex and his bubble/chat are excluded from screen capture and screen sharing (`HideFromScreenCapture`)
- [x] "What was I doing?" answered from the timeline (context sent with each chat message)
- [x] Matte fabric redesign + cursor-driven head turn (design handoff)
- [x] Idle activities: sip, phone, book, sit against the screen edge, stretch, watch
- [x] Knock-on-the-glass nudges that persist until answered
- [x] Stops moving while the mouse is over him or his bubble; bubble always wholly on one monitor

### v0.3: "He helps" (built 2026-10-12, except share-a-window)
- [x] Lab Notebook to-dos and journal (read + inbox actions from chat); end-to-end tested 2026-10-12
- [x] Notebook v2 on Vex's side: journal checkboxes, "Vex" project pages, memory migration. **Waiting on the notebook v2 rebuild.**
- [x] Calendar: classic Outlook over COM when running, else Power Automate `calendar.json` in OneDrive; 10/2-min reminders, "aren't you meant to be in…"
- [ ] Share-a-window to ask about what's on screen
- [x] Memory pages (now `memory\*.md`, owned by Vex); nudge intervals adapt to Done/Later/✕/ignored
- [x] End-of-day nudge → summary in chat, offer to add it to the journal
- [x] Chat actions: to-do add/done, journal, reminders, remember

### Shareable (2026-10-12)
- [x] First-run setup (name, workplace, notes location, start with Windows, knock sound, Claude Code alerts, activity log on/off, privacy note); tray → Setup… to change
- [x] No personal details in the code: name and workplace come from config
- [x] Notes store: Lab Notebook if detected, else a local folder of markdown files (`Journal\YYYY-MM-DD.md`, `Vex\*.md`) with the same to-do/notes/pages behaviour
- [x] README for colleagues; share the single `Vex.exe`
- [ ] Code signing (avoids the SmartScreen warning) / GitHub Releases

### Workshop (2026-10-13)
- [x] Ask in chat for a new ability → coder session writes it, Vex builds it, a second session reviews it, commit + push to main, knock, self-swap with rollback (section 9)
- [ ] Capability review before sharing any build that has a workshop

### Comfort (2026-10-13)
- [x] Copy the chat as text or a picture
- [x] Light mode / match Windows; hoodie colour from the tray

### He learns (2026-10-13)
- [x] Build/restart from chat need a click (prompt-injection guard); context marked as data, not instructions
- [x] Exempt nudges wait while something's full screen instead of being dropped
- [x] Vex owns his memory (`memory\*.md`); the notebook mirrors it (notebook side to build)
- [x] A fresh chat each day; daily chat archive; nightly diary entry + learned facts filed onto his pages
- [x] Curiosity: asks about recurring things in window titles; reads window text for allowlisted apps
- [x] Pack-up nudge with TomTom traffic (or a Google Maps link), `[[traffic]]` in chat
- [x] Morning brief, "Prep me" on meeting reminders, quick-chat hotkey (`QuickChatHotkey`, Ctrl+Alt+V)
- [ ] Notebook mirror of the memory folder (Lab Notebook session)
- [ ] Streamed chat replies; weekly review; workshop cancel/undo; Claude Code reaction animations

### v0.4: "He talks"
- [ ] Voice in and out
- [ ] Try a local model via Ollama

### Later: "He follows"
- [ ] Android companion (shared memory over OneDrive or Bluetooth)
- [ ] Perching on title bars, seasonal outfits
- [ ] More context animations (ideas): tiny laptop while Claude works, foot-tap while it waits on you, blowing on fresh coffee, peeking round a window edge, legs dangling off a title bar, doodling during long meetings, wave when you're back, shiver when the mug's cold

## 12. Open questions

- Calendar: nothing connected yet. Classic Outlook arrives with the new laptop (works automatically when it's running); Power Automate `calendar.json` is the fallback if wanted sooner.
- The workshop coder can run `dotnet build`, which runs whatever the project file says, so its sandbox isn't airtight. The reviewer and Ben are the real check; what matters is that it only changes Vex's own repo.
- Memory use (~285 MB, mostly drawing effects in software rendering); slim down if it matters.
- Not yet seen in real use: wellbeing nudge timings over a full day, meeting reminders, a live Claude Code permission prompt, full-screen hiding.
