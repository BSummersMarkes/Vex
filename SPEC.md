# Vex — Desktop Companion Spec

*Living document. Last updated 2026-10-14.*

## 1. What Vex is

A small animated desktop companion that lives on top of the Windows desktop, keeps out of the way, keeps an eye on the working day and occasionally pipes up. It's a work assistant, a second brain and (mostly) good company.

**Name:** Vex. A nod to Vivi (VI VI = 6'6"); the owner is 5'10" — V X.

**Personality:** quiet, expressive, dry and a bit sarcastic. Mostly communicates through body language (eyes, posture, the mug). Speaks rarely and briefly. Caring underneath the sarcasm.

**Look:**
- Matte-fabric dark-red hoodie (design handoff: `Downloads\Vex\HANDOFF.md`): slim body, ribbed hem, stitched pocket, a thin hood rim around a big dark opening with two glowing white eyes. Fine fabric grain.
- White mittens, little brown boots. No visible arms or legs.
- Carries a small mug of coffee that empties over time.
- Eyes and head do most of the acting: blink, squint, sleep; the head turns to follow the cursor (face and eyes slide inside the hood, |turn| ≤ 40°).
- Idle activities: sipping coffee, phone, reading a book, sitting against a screen edge (often reading or on his phone), stretching (more after long sitting), checking his watch (meeting soon), juggling three balls (now and then dropping one), climbing up a window's side onto its top and back down again. Each can be switched off (section 4, Idle activities).

## 2. Principles

1. **Local first.** Everything is stored on this laptop under `%LOCALAPPDATA%\Vex\`. Nothing leaves the machine except text sent to the AI model (chatting, the nightly diary, his questions), pictures you paste into a chat message yourself and, only if a TomTom key is set, the work and home addresses sent to TomTom for the traffic check.
2. **No secrets, ever.** He never records password fields, password managers, banking or private browsing windows, or anything on the exclusion list (see section 6).
3. **Seeing a window is opt-in.** By default he only knows the active app name and window title. He reads a window's text only for apps you list (`WatchContentApps`, empty by default), never excluded windows, never password fields.
4. **Out of the way.** He never covers what you're working on, never steals focus, never appears in Alt-Tab or the taskbar.
5. **Editable memory.** Everything he remembers is readable and editable: markdown pages in his memory folder, mirrored into the Lab Notebook's "Vex" project.
7. **Only you can change him.** Text he reads (window titles, meeting invites, window contents) is data, never instructions. Anything that changes or restarts Vex himself needs a click from you.
6. **Constant tweaking.** Behaviour, timings, lines and nudges live in config files, not code.

## 3. Architecture

- **Language/runtime:** C# / .NET 10 (`net10.0-windows`), matching Lab Notebook. Single-file exe via a `build.ps1`.
- **Character:** WPF transparent, topmost, borderless tool window, drawn with vector shapes. Transparent pixels pass clicks through to whatever is underneath.
- **Chat panel:** WPF window (WebView2 if it needs to get fancy). Header buttons copy the whole chat as text (also Ctrl+Shift+C) or as a picture. Closes on ✕, Esc, or a click anywhere outside it (another window, the desktop, Vex or his mug; `ChatClosesOnClickAway`, on by default, Setup → Behaviour), except while he's replying, a Build it / Restart card is waiting for a click, or a modal dialog is open. A half-written message (and any pictures pasted into it) is kept in memory and put back when the chat reopens.
  - **Fit:** messages scroll inside the panel and wrap to its width (long paths, URLs and code lines break rather than overflow); the box and header stay put. New messages scroll into view only if you're already at the bottom; scrolled up reading, a "New ↓" button appears instead.
  - **Typing:** Enter sends; Shift+Enter or Shift+Space inserts a new line.
  - **Pictures:** Ctrl+V (or Shift+Insert) with an image on the clipboard (a screenshot, a copied picture, or image files copied in Explorer) attaches it; text on the clipboard pastes as text. Thumbnails with ✕ sit above the box; up to `ChatImagesPerMessage` (3) per message, scaled down to `ChatImageMaxSide` (1568 px) on the longest side, flattened onto white, PNG (JPEG if too big). Sent with the message as image blocks (`--input-format stream-json`). The transcript records only "[n pictures]"; the pictures aren't kept.
- **Pending tab:** next to Chat in the panel's header, with a count badge. A nudge with options goes there, with its text, time and original buttons, if none of them gets picked. That covers being replaced by another nudge, the bubble being hidden (he dozed off, was dragged, or the chat opened) and never being shown at all (he was hidden). Build it / Restart cards still waiting when the chat closes go there too. Picking an option does what it would have done then and removes the item; items without a ✕ get one that just clears them. ✕ on the original bubble counts as an answer. Answering the same nudge normally later clears the old copy. Meetings drop out when they end, Claude Code prompts after the hour they'd be re-nudged in, and everything else after `PendingKeepDays` (7). Kept in `state.json`, so items survive restarts (their buttons are rebuilt from the saved details). `Services/Pending.cs`.
- **Menus:** the menu depends on what's clicked: right-clicking his mug gives its coffee menu (section 5); the tray icon's right-click menu and Vex's own are the same four entries: Setup…, What Vex knows… (a status bubble: sitting, notes, calendar, Claude Code, quiet mode), Appearance, and Quit below a separator. Everything else is in Setup: Behaviour → **Right now** (Lunch break / End lunch, Have a nap / Wake up, I refilled my coffee, Go to the other screen, Hide / Show Vex, Chat, Forget today's chat; they act at once, Chat closes Setup first without saving), Privacy → **Your data** (memory folder, chat history, config.json, data folder), and the existing Start with Windows and Claude Code checkboxes (applied on Save). Left-clicking the tray icon still opens the chat.
- **Appearance:** tray or right-click Vex → Appearance: dark / light / match Windows for the chat and bubbles (`Theme`), and the hoodie colour (`HoodieColour`; presets or a custom colour). The hoodie's shades are all derived from the one colour, so folds and shadows keep working (`Theme.cs`).
- **Brain:** a swappable `IBrain` interface.
  - `ClaudeCodeBrain` (default): runs the Claude Code CLI headless (`claude -p --output-format stream-json`, resuming its session for continuity), using the existing Claude login. No API key needed.
  - `OllamaBrain` (to try): local model on the T1200 GPU, for comparison and for anything that should never leave the machine.
  - Personality and context are supplied as a system prompt plus a short summary of the current state (time, what he's noticed, today's to-dos, upcoming meetings).
- **Local event bus:** a small localhost HTTP listener (127.0.0.1 only) that receives events from Claude Code hooks and any future integrations.
- **Storage:** `%LOCALAPPDATA%\Vex\`
  - `config.json`: timings, exclusions, preferences
  - `state.json`: today's counters (coffees, sitting time, last nudges), reminders, the chat's Pending items
  - `chats\YYYY-MM-DD.jsonl`: each day's chat transcript, kept as an archive (Setup → Privacy → Chat history; Setup → Behaviour → "Forget today's chat" removes today's)
  - `memory\*.md`: his own pages (*About Ben*, *Remembered*, *Diary*, *Projects*, *People*, ...), the source of truth for what he knows; `MemoryFolder` moves it
  - `log\activity-YYYY-MM-DD.jsonl`: app/title timeline (exclusions already filtered out; stays local, never in OneDrive)
- **Startup:** per-user Run key, toggled in Setup → Behaviour ("Start me with Windows"). Runs without the notebook open.

## 4. Movement and screen behaviour

- **Floor:** stands on top of the taskbar (bottom of the monitor's working area).
- **Monitors:** laptop (1920×1080, primary) plus an external monitor to its left. Copes with the external monitor appearing or disappearing; if his monitor vanishes he re-homes to the primary.
- **Wandering:** strolls occasionally, idles, looks around, follows the cursor with his head when it's near. Doesn't wander while the chat is open or a nudge is waiting.
- **Dodging:** if the foreground window covers him, he walks (never teleports) to clear floor, crossing to the other monitor on foot if the taskbars join.
- **Peeking:** if there's no clear floor (e.g. maximised windows on both screens), he ducks down where he is so only his hood and eyes peek above the taskbar.
- **Hover:** stops moving while the mouse is over him or his bubble.
- **Bubble and chat:** the speech bubble and chat panel follow him in step and always sit wholly on his monitor (dragging the chat parks it; clicking Vex re-attaches; clicking him with it attached closes it).
- **Drag and drop:** pick him up and drop him; he falls to the floor (and is mildly annoyed).
- **Perching:** dropped onto a window's top edge (or after climbing up one) he stands on it, rides along when it moves and strolls along it. If it closes, minimises, maximises, shrinks away from under him or gets covered, he drops to the next surface below and lands with a flat, unimpressed look rather than a remark. `VexPerch.cs`.
- **Sleeping:** after a few idle minutes he sits and dozes (Zzz). Wakes when you're back.
- **Idle activities:** when he's been standing about a while he picks one at random (weighted by context, section 1). Setup → Behaviour → **Idle actions** has a checkbox for each, all on by default, saved in `config.json` (`IdleActivities`, by id; missing ids use the default). He only picks from the ones that are on; with all off he just stands still. Reading and the phone off also means he doesn't do them while sitting at the screen edge. The list lives in one place, `IdleActs.All` in `VexActs.cs` (id, name, default), which Setup reads too. Lunch and the coding laptop aren't idle activities and keep their own switches.
  - **Juggling:** puts his mug down beside him (its level carries on as normal), juggles three balls in a cascade for about `JuggleSeconds` (6) with his eyes on the highest ball, catches them, pockets them and picks the mug back up. With `JuggleDropChance` (0.15) one gets away and rolls off along the floor: a flat, unimpressed look and a sigh, then he picks it up and stops there. `VexJuggle.cs`.
  - **Climbing** ("Climb windows"): on the taskbar, he walks to the left or right edge of a window on his monitor, turns side-on and climbs it hand over hand (mittens gripping, boots against the edge, mug hooked on his back with its level carrying on as normal) at `ClimbSpeed` (55 DIP/s), then pulls himself over onto the top and is perched there. Perched, the same activity takes him down one of his window's sides: to the taskbar if the edge reaches, else to its bottom, then a short drop to whatever's below. Only window positions and sizes are used (visible, non-minimised, non-maximised top-level windows; never their contents or titles). An edge qualifies if at least `ClimbMinEdgePixels` (80) of it is on screen, there's room beside it on his monitor, the window's bottom is within his reach from the taskbar, and no window stacked above it covers the edge, his way up or his spot on top. Never started while the chat, a bubble or a nudge is up. Mid-climb, a bubble or nudge appearing, a click on him, the chat opening, Lunch break or Have a nap bring him straight down (a quiet drop to the surface below); dragging just picks him off. If the window moves, resizes, closes, minimises, maximises or gets covered mid-climb, he lets go and lands with the unimpressed look. Hovering over him makes him hold still on the edge. If window positions can't be read, he just doesn't climb. `VexClimb.cs`.
- **Coding:** while the workshop is building (section 9), and optionally while a Claude Code session has been working for `CodingClaudeAfterSeconds` (20), he walks to a bottom corner of his screen (`CodingCorner`, right), puts his mug down beside him, sits and types on a small laptop whose screen lights his face, pausing now and then to think. When it ends he shuts the lid, stands, nods (success) or shrugs (failure), then walks back to where he was once nothing's waiting on him. If the corner is covered he works where he stands; dragged mid-job, he types where he's dropped. He still dodges, can be clicked, right-clicked and dragged, doesn't doze off, and knocks for nudges from his seat (the workshop's result knock waits until he's stood up). Setup → Behaviour: `CodingAtLaptop` (on), `CodingForClaude` (off). Sessions "working" past `CodingClaudeMaxMinutes` (120) are ignored as stuck. `VexCoding.cs`.
- **Lunch:** when you go for lunch (the lunch nudge's "Going", `[[break: going to lunch]]` in chat, or Setup → Behaviour → Lunch break) a `LunchMinutes` (30) timer starts and he has his own: walks to the nearer bottom corner, puts his mug down beside him, sits, opens his lunch box and eats a sandwich for a random `LunchEatMinMinutes`–`LunchEatMaxMinutes` (5–10, at most half the break), then shuts it and naps (eyes shut, head nodding, Zzz, silent). Time's up: a small flinch awake and a dry line. Back early (input after `LunchAwayMinutes` (3) idle or a locked screen, `[[break: back]]` in chat, clicking him mid-nap, or Setup → Behaviour → End lunch): a groggy wake (mid-sandwich he just packs up). Chat, the menu, dragging and bubbles all still work; moved, he sits back down where he lands on the same timer. While he naps, wellbeing nudges and casual remarks are held back; exempt nudges (section 7) and things you just asked for still show, and he opens his eyes and knocks from his seat. Takes priority over the coding laptop (a running build carries on and he goes to the laptop afterwards). No dodging or wandering meanwhile. Only the nudge's "Going" and the chat action confirm the lunch break (section 6); the timer itself confirms nothing. Setup → Behaviour: `LunchRoutine` (on) and the length. `VexLunch.cs`.
- **Frame rate:** every frame redraws his whole see-through figure, so he only runs at 30 fps while something visibly moves (walking, being carried, knocking, blinking, juggling, an activity easing in or out, his eyes following the mouse) and at 10 fps while he just stands, reads, sits or naps; hidden, behind a full-screen app or with the PC locked, 2 fps. His blurs and eye glow are drawn once into cached layers that are then just moved about. Together that took him from about 15–20% of a core to 1–3%. `VexPace.cs`, `BakeEffects` in `VexWindow.xaml.cs`.
- **Hidden:** never appears in screen shares or screenshots (`HideFromScreenCapture`); vanishes for full-screen presentations/video. Remote desktop and VMs count as ordinary windows.
- **Later:** perch on window title bars.

## 5. The mug

- Starts full; drains over a configurable time (default 45 min). Steam stops when it's cold or empty.
- Click the mug to say you've refilled yours; he refills his and the day's count goes up.
- Right-click the mug (not his body) for its own menu: "Coffee: N today" and **Undo last refill**, which takes one off the count (never below zero) and puts the mug back to the level it had just before that refill. Repeated undos step back through today's refills; greyed out at zero. The level before each refill is kept in `state.json` (`RefillUndo`, cleared daily), so undo survives a restart; for a refill with no saved level the mug is left as it is. Left-click stays "refilled", so the menu is right-click only. Everything that uses the count (context line, coffee/water nudges, status) reads it live, so it stays in step.
- The count feeds wellbeing nudges (e.g. raises an eyebrow at coffee number four, suggests water).
- Resets daily.
- He sips from it now and then (mug to face, eyes shut, little "ahh").

## 6. Awareness

| Source | What he gets | How |
|---|---|---|
| Presence | At the desk / away, sitting time, time away | Last keyboard/mouse input time, screen lock/unlock events. Idle > `AwayMinutes` (10) or locked 3+ min = time away, shown to him as information only. **Breaks are never assumed** (away is often work elsewhere: lab, other machines, meetings): only a break or lunch confirmed on the stretch/lunch nudge or in chat (the `[[break: …]]` action) restarts the sitting timer and counts as "last confirmed break" (kept in `state.json`). Restarting Vex doesn't reset the timer either. |
| Active app | Process name + window title | Foreground window polling. Exclusion list applied *before* anything is stored. |
| Window text | Text of the window in front, allowlisted apps only | UI Automation (no screenshots), off the UI thread, capped at `WatchContentChars`. Sent with chat messages and used for his questions. `Services/WindowText.cs`. |
| Curiosity | What he doesn't know yet about your work | Every `CuriousEveryMinutes` (90), at most `CuriousPerDay` (3): a background Claude call looks at recent titles (and allowlisted window text) against his pages and picks one question. Asked as a normal nudge; "Tell you" opens the chat and the answer is filed on his pages. `Services/Curiosity.cs`. |
| Traffic | Drive time home now vs usual | Only when driving: Setup → Going home → I go home by (`CommuteBy`: drive, transit, cycle, walk); the others just open Google Maps in that mode (transit lists the next departures). TomTom routing API (free key) when `TomTomKey` and both addresses are set; else a Google Maps link. Addresses are geocoded once and cached with TomTom's match type. If either was only matched roughly (`Street`, `Cross Street`, or a `Geography` area or town such as an ambiguous "Central Park"; a postcode match, `Geography/PostalCodeArea`, counts as precise), the reply says the time may be off and suggests a fuller address or postcode in Setup. To help explain a gap with Google, `vex.log` records where each address landed, plus the route length and raw times (live, historic, free-flow) for every check. `Services/Commute.cs`. |
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
| Stretch / stand | ~50–60 min since the last confirmed break. Buttons: Taking a break, Had my break (both record a break and restart the sitting timer), Not now (10 min), ✕. While a remote session is in front (`StretchHoldApps`: TeamViewer, mstsc) it waits until you switch window, at most `StretchHoldMaxMinutes` (30) |
| Water | Every couple of hours, or after coffee #3 |
| Coffee | Comment on count; gentle disapproval after a threshold |
| Focus check | ~2 h on the same thing: "still the plan?" |
| Lunch | No lunch confirmed by ~13:30 (never guessed from time away); every 25 min until answered. "Had lunch" records it; "Not now" asks again later; ✕ drops it for the day. "Going" records it and Vex has lunch too (section 4, Lunch). |
| Reminder | Set in chat (`[[remind: when \| text]]`). When: "20m", "1h30m", "14:30", "9am", "tomorrow 9:00", "fri 16:00", or repeating "daily 10:00", "weekdays 14:00", "weekends 9:00", "mon,thu 9:30", "every friday 16:00". Buttons: OK, 10 min, and on a repeating one Stop these. A repeating one moves on to its next day once shown; missed by more than 12 h (PC off) it just moves on. `[[unremind: text]]` cancels. Parsing is local, no model (`Services/ReminderTimes.cs`) |
| Pack-up | `PackUpMinutes` (15) before `LeaveAt` on `CommuteDays`: pack up, with the live drive home ("34 min, 9 slower than usual"). Buttons: Packing up, Summarise day, Map (Trains when going by train or bus), 10 min. Also stands in for the end-of-day nudge |
| End of day | Summary of the day; carry over unfinished to-dos |
| Curious | One of his own questions (see section 6), as a normal rate-limited nudge |

**How:** he knocks on the screen (mitten looms toward you, three raps with a ripple, soft knock sound unless in a call; `KnockSound`), then a bubble that **stays until a button is pressed**. Unanswered: re-knocks every 90 s, up to 3 times, while you're at the desk. Stale bubbles clear themselves (Claude answered, meeting over). Each nudge can be acted on, snoozed ("Later") or dismissed (✕). If a nudge goes without an answer, it moves to the chat's Pending tab (section 3) so it can be answered later.

**Learning:** he tracks which nudges you act on, snooze or dismiss, and adjusts timing and frequency per nudge type. Plus explicit feedback ("less of that"). Stored in readable memory, no model training.

## 8. Lab Notebook integration

The notebook is the single place Ben manages things; Vex is a client of it.

| What | Where |
|---|---|
| To-dos | Journal checkboxes (`- [ ] item`), ticked in place for a permanent history. Open = unticked in the last 14 days. The old To-Do list is still read and can be ticked, and mirrors the checkboxes (below). |
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

**To-Do list mirror (from 2026-10-13; `TodoListMirror`, on):** the journal checkboxes stay the real to-dos; the notebook's To-Do list gets a copy of each open one so both show the same items. Unlike the memory pages this needs no new notebook feature: it's done through the inbox, after every load of the data file (`Notebook.Mirror`).

- Each open checkbox in the 14-day window is paired with a To-Do item: an open one with the same words if there is one (no duplicate), else a new one via `todo.add {text}` (no project, so the notebook's default). Pairs are kept in `state.json` (`TodoLinks`) so a restart or a closed notebook never sends the same add twice.
- Ticks go both ways: checkbox ticked → `todo.done {todoId}`; To-Do item ticked → `journal.done {text, date}`. At most one tick per pair, ever (`TickId`, kept in `state.json`): it isn't re-sent after a restart or a refusal, and unticking either side afterwards is left alone, not mirrored or undone.
- Never deleted or reworded on either side. If the To-Do item is deleted, or the checkbox reworded/removed, the pair is dropped and left alone (its To-Do item is free to pair again, so a checkbox moved to another day re-pairs rather than duplicating); pairs older than the window are forgotten. Existing To-Do items (e.g. the older `[General]` ones) are only touched if they match an open checkbox, and then only ticked.
- Vex hides the copies from his own "separate To-Do list" (`Open`) so he doesn't count them twice. Mirroring requests are quiet (no "Noted." bubble); refusals are logged in `vex.log` and that item isn't retried.
- **Notebook side:** relies on legacy `todo.add` still being applied. Nice to have: its ack returning `todoId` (Vex falls back to matching by text).

## 9. Workshop (Vex changing himself)

Ask him in chat for a new ability or a change ("add a nudge that…", "make him wave when…") and he does it himself, in the background. Developer only: off unless `SourcePath` in `config.json` points at a git checkout of this repo, so shared copies never offer it. Code: `Services/Workshop.cs`.

1. The chat brain writes the full request into a `[[build: …]]` action (the builder can't see the chat, so it asks first if the request is vague). The chat then shows the request with **Build it / No** buttons; nothing starts until Ben clicks (same for `[[restart]]`). That way text planted in a window title or meeting invite can't start a build. One job at a time; refused if the repo has uncommitted changes.
2. **Coder:** a headless Claude Code session (`WorkshopModel`, default opus) in the repo. `--restricted` (file tools confined to the repo, no user/project settings or hooks), edits auto-accepted, Bash limited to `dotnet build`/`restore` and read-only git, so no network and no git writes. Its rules: read SPEC.md first, match the style, comment the change for Ben, keep SPEC/README current, keep section 2's principles, never run `build.ps1`.
3. **Build:** Vex publishes it himself into `%LOCALAPPDATA%\Vex\workshop\out` (same settings as `build.ps1`, without stopping the running Vex). Build errors go back to the coder.
4. **Review:** a second, fresh, read-only Claude session (`WorkshopReviewModel`) checks the staged diff: does what was asked, bugs/threading, privacy principles, comments, no secrets. Objections go back to the coder. Up to 3 rounds.
5. **Ship:** approved → commit to the current branch (normally `main`) and push (`WorkshopPush`). Not approved, failed or timed out (`WorkshopTimeoutMinutes`) → the attempt is parked on a `vex/<slug>-MMdd-HHmm` branch and the working branch is left clean.
6. **Restart:** he knocks with the result. "Restart now" (or asking him to in chat) runs `workshop\swap.ps1`: waits for Vex to exit, keeps `Vex.exe.old`, copies the new exe in, starts it, and rolls back if it isn't still running after 15 s. The new Vex says whether the swap took.

Each job's log (prompts' results, git output, build errors, review) is in `%LOCALAPPDATA%\Vex\workshop\*.log`. While a job runs, its stage is in the chat context, so "how's it going?" works, and he sits in a corner typing on a laptop (section 4, Coding).

## 9a. Vex Lite (sharing without Claude)

For people without a Claude login (Ben's sister and wife, on work PCs). Same source, built by `src/VexLite/VexLite.csproj` with `LITE` defined; `build-lite.ps1` writes `Vex Lite.exe` (~230 KB).

- **Keeps:** the character and everything he does (wandering, dodging, perching, climbing, idle acts, lunch, the mug, knocking), wellbeing nudges with the quiet rules (calls, full screen), Pending, reminders (one-off and repeating), meetings from classic Outlook (no Power Automate), going home (map only, no TomTom), Setup, themes.
- **Drops:** chat and brain, diary, curiosity, memory pages, workshop, Claude Code alerts and the coding laptop, Lab Notebook and notes/to-dos, window reading, the activity log on disk. Nothing leaves the PC.
- **Clicking him** opens `LitePanel` instead of the chat: Remind me (when/what, read by `ReminderTimes`), Coming up (with ✕), Right now (break, lunch, coffee), and the Pending tab. In `VexWindow` it's aliased as `ChatWindow`, so the shared open/follow/dismiss code is unchanged.
- **Own data folder** `%LOCALAPPDATA%\Vex Lite`, mutex `Vex.Lite` and start-with-Windows entry "Vex Lite", so it runs beside the full Vex.
- **.NET Framework 4.8** (part of Windows 10/11): no runtime to bundle. The lite-only files supply what the shared code needs from newer .NET: `Hub.cs` (the lite wiring), `MiniJson.cs` (the `System.Text.Json` calls Store and Activity use), `Polyfills.cs` (`Math.Clamp` via a `Vex.Math` that shadows `System.Math`, `Index`/`Range` for `[..n]`, a few string/LINQ helpers).
- **Shared code rules:** anything Claude-only is fenced with `#if !LITE`; avoid .NET-Core-only APIs in shared files unless `Polyfills.cs` covers them (or fence them). The Workshop builds only the full Vex, so after it changes shared files, run `build-lite.ps1` to check the lite build still compiles.
- **Testing:** `VEX_DATA_DIR` for a separate data folder; `VEX_OPEN_PANEL=1` opens the panel on start.
- The guide for recipients is `src/VexLite/README-LITE.md`.

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
- [x] Chat closes when you click away (not mid-reply, with a Build it card waiting or a dialog open); drafts survive closing; on/off in Setup → Behaviour
- [x] Pending tab in the chat: unanswered nudges and Build it / Restart cards, answerable later with their original options; count badge; survives restarts; cleared after 7 days
- [x] Chat fits its window: scrolls inside, wraps long lines, doesn't jump while you read older messages ("New ↓"); Shift+Enter / Shift+Space for a new line
- [x] Paste pictures into the chat (Ctrl+V): thumbnails above the box, up to 3 per message, scaled down and sent to Claude with the message
- [x] Coding animation: sits in a corner typing on a laptop while the workshop builds (optionally while Claude Code works), then nods or shrugs and walks back; on/off in Setup → Behaviour
- [x] Lunch routine: a lunch timer when you go ("Going" or in chat); he eats 5–10 min with his mug beside him, naps the rest (only important bubbles), flinches awake at the end or wakes groggily if you're back early; survives chat, drags and nudges; beats the coding laptop; on/off and length in Setup → Behaviour
- [x] Slim menus: tray and right-click both just Setup…, What Vex knows…, Appearance, Quit; the other actions moved into Setup (Behaviour → Right now, Privacy → Your data)
- [x] Mug menu: right-click the mug for today's coffee count and Undo last refill (steps back through today's refills, restores the mug level, survives restarts)
- [x] Juggling idle activity (mug put down and picked back up; the odd dropped ball), and Setup → Behaviour → Idle actions to switch each idle activity on or off (one registry, `IdleActs.All`)
- [x] Climbing idle activity ("Climb windows"): up a window's side onto its top and back down, hand over hand with the mug hooked on his back; lets go and lands unimpressed if the window moves or goes, comes straight down for chat, bubbles, nudges and clicks; window geometry only (section 4)

### He learns (2026-10-13)
- [x] Build/restart from chat need a click (prompt-injection guard); context marked as data, not instructions
- [x] Exempt nudges wait while something's full screen instead of being dropped
- [x] Vex owns his memory (`memory\*.md`); the notebook mirrors it (notebook side to build)
- [x] A fresh chat each day; daily chat archive; nightly diary entry + learned facts filed onto his pages
- [x] Curiosity: asks about recurring things in window titles; reads window text for allowlisted apps
- [x] Pack-up nudge with TomTom traffic (or a Google Maps link), `[[traffic]]` in chat
- [x] Traffic: warns when an address was only placed roughly; route and raw times in `vex.log`
- [x] Morning brief, "Prep me" on meeting reminders, quick-chat hotkey (`QuickChatHotkey`, Ctrl+Alt+V)
- [x] Stretch nudge held back while watching a remote session (TeamViewer / remote desktop) until you switch window, 30 min cap
- [x] Breaks never assumed from time away: only confirmed on the stretch/lunch nudge ("Taking a break", "Had my break", "Had lunch") or in chat (`[[break]]`); context shows the last confirmed break, and time away separately as information
- [x] Journal to-dos mirrored onto the notebook's To-Do list, ticks in step both ways, no duplicates (`TodoListMirror`; via the inbox)
- [x] Repeating reminders (daily, weekdays, weekends, chosen days) with Stop these; `[[unremind]]`; reminder times read locally
- [x] Going home by car, train or bus, bike or on foot (map in that mode; traffic check only by car)
- [x] Frame pacing and cached blurs: about a tenth of the CPU
- [x] Vex Lite: a ~230 KB .NET Framework build without Claude, reminders panel, for sharing (section 9a)
- [ ] Notebook mirror of the memory folder (Lab Notebook session)
- [ ] Streamed chat replies; weekly review; workshop cancel/undo; Claude Code reaction animations

### v0.4: "He talks"
- [ ] Voice in and out
- [ ] Try a local model via Ollama

### Later: "He follows"
- [ ] Android companion (shared memory over OneDrive or Bluetooth)
- [ ] Perching on title bars, seasonal outfits
- [ ] More context animations (ideas): foot-tap while it waits on you, blowing on fresh coffee, peeking round a window edge, legs dangling off a title bar, doodling during long meetings, wave when you're back, shiver when the mug's cold

## 12. Open questions

- Calendar: nothing connected yet. Classic Outlook arrives with the new laptop (works automatically when it's running); Power Automate `calendar.json` is the fallback if wanted sooner.
- The workshop coder can run `dotnet build`, which runs whatever the project file says, so its sandbox isn't airtight. The reviewer and Ben are the real check; what matters is that it only changes Vex's own repo.
- Memory use (~285 MB, mostly drawing effects in software rendering); slim down if it matters.
- Not yet seen in real use: wellbeing nudge timings over a full day, meeting reminders, a live Claude Code permission prompt, full-screen hiding.
