using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace Vex;

/// <summary>Tweakable settings. Written out with defaults on first run; edit %LOCALAPPDATA%\Vex\config.json.</summary>
public class VexConfig
{
    public double MugDrainMinutes { get; set; } = 45;
    public double MugSteamMinutes { get; set; } = 15;
    public double SleepAfterMinutes { get; set; } = 5;
    public double WalkSpeed { get; set; } = 70;          // DIP per second
    public string Model { get; set; } = "sonnet";
    public string? ClaudePath { get; set; }
    /// <summary>What Vex calls you. Asked on first run; defaults from the Windows account name.</summary>
    public string UserName { get; set; } = "";
    /// <summary>Optional, e.g. "Markes International". Only used to give Claude a bit of context.</summary>
    public string Workplace { get; set; } = "";
    /// <summary>"auto" (Lab Notebook if found, else the local folder), "notebook" or "local".</summary>
    public string NotesMode { get; set; } = "auto";
    /// <summary>Folder for local notes (markdown files). Default: Documents\Vex Notes.</summary>
    public string? NotesFolder { get; set; }
    /// <summary>Keep the app/window-title timeline on disk and share window titles with Claude when chatting.</summary>
    public bool ActivityLog { get; set; } = true;
    /// <summary>First-run setup has been completed.</summary>
    public bool SetupDone { get; set; }

    /// <summary>Chat panel and speech bubble: "dark", "light" or "system" (follow Windows). Tray → Appearance.</summary>
    public string Theme { get; set; } = "dark";
    /// <summary>Hoodie colour as #RRGGBB; empty = the original dark red. Tray → Appearance.</summary>
    public string HoodieColour { get; set; } = "";

    /// <summary>Keep Vex (and his chat) out of screenshots and screen shares.</summary>
    public bool HideFromScreenCapture { get; set; } = true;

    // Presence and nudges (minutes unless noted).
    public double AwayMinutes { get; set; } = 10;           // idle this long (or locked 3+ min) is noted as time away; never a break
    public double NudgeGapMinutes { get; set; } = 15;       // at most one wellbeing nudge per gap
    public double EyesEveryMinutes { get; set; } = 20;
    public double StretchEveryMinutes { get; set; } = 55;
    /// <summary>Remote-session apps (process names) where a due stretch nudge waits until you switch to another window.</summary>
    public List<string> StretchHoldApps { get; set; } = new() { "TeamViewer", "mstsc" };
    /// <summary>Longest a stretch nudge is held back for those apps before it's shown anyway.</summary>
    public double StretchHoldMaxMinutes { get; set; } = 30;
    public double WaterEveryMinutes { get; set; } = 120;
    public double FocusCheckMinutes { get; set; } = 120;
    public string LunchBy { get; set; } = "13:30";
    /// <summary>When you go for lunch, Vex has his own (eats, then naps for the rest) on a timer. Setup → Behaviour.</summary>
    public bool LunchRoutine { get; set; } = true;
    /// <summary>How long that lunch timer runs. Setup → Behaviour.</summary>
    public double LunchMinutes { get; set; } = 30;
    /// <summary>He eats for a random time between these (never more than half the break), then naps.</summary>
    public double LunchEatMinMinutes { get; set; } = 5;
    public double LunchEatMaxMinutes { get; set; } = 10;
    /// <summary>Idle this long (or locked) during his lunch means you've gone; your next input then wakes him early.</summary>
    public double LunchAwayMinutes { get; set; } = 3;
    public string EndOfDay { get; set; } = "17:15";
    public int[] MeetingWarnMinutes { get; set; } = { 10, 2 };

    /// <summary>Morning brief on the first sight of you each day: meetings, to-dos, what carried over.</summary>
    public bool MorningBrief { get; set; } = true;
    /// <summary>Global hotkey that opens the chat from anywhere (e.g. "Ctrl+Alt+V"). Empty = none.</summary>
    public string QuickChatHotkey { get; set; } = "Ctrl+Alt+V";
    /// <summary>Close the chat when you click somewhere else (unless he's replying or there's a half-written message).</summary>
    public bool ChatClosesOnClickAway { get; set; } = true;
    /// <summary>How long an unanswered nudge or card waits in the chat's Pending tab before it's cleared.</summary>
    public double PendingKeepDays { get; set; } = 7;
    /// <summary>How many pictures can be pasted into one chat message.</summary>
    public int ChatImagesPerMessage { get; set; } = 3;
    /// <summary>Pasted pictures bigger than this (pixels, longest side) are scaled down before they're sent to Claude.</summary>
    public int ChatImageMaxSide { get; set; } = 1568;

    // Coding at the laptop (VexCoding.cs).
    /// <summary>While his workshop is building, he sits in a corner and types on a laptop. Setup → Behaviour.</summary>
    public bool CodingAtLaptop { get; set; } = true;
    /// <summary>Also sit at the laptop while one of your Claude Code sessions is working (needs the Claude Code hooks).</summary>
    public bool CodingForClaude { get; set; } = false;
    /// <summary>"right" or "left": the bottom corner of his screen he works in.</summary>
    public string CodingCorner { get; set; } = "right";
    /// <summary>A Claude Code turn has to run this long (seconds) before he fetches the laptop, so quick answers don't send him off.</summary>
    public double CodingClaudeAfterSeconds { get; set; } = 20;
    /// <summary>A session still "working" after this many minutes is assumed stuck (its Stop hook never came), so he packs up.</summary>
    public double CodingClaudeMaxMinutes { get; set; } = 120;

    // Memory and the daily diary.
    /// <summary>Vex's own pages (markdown). Null = %LOCALAPPDATA%\Vex\memory. The Lab Notebook mirrors this folder.</summary>
    public string? MemoryFolder { get; set; }
    /// <summary>Writes each day's diary entry (and files what he learned) from the chat and activity, the next morning.</summary>
    public string DiaryModel { get; set; } = "sonnet";

    // Watching: curiosity about what you work on, and (allowlisted apps only) reading the window in front.
    /// <summary>Now and then, asks you about something he keeps seeing in window titles and doesn't know yet.</summary>
    public bool Curious { get; set; } = true;
    public double CuriousEveryMinutes { get; set; } = 90;
    public int CuriousPerDay { get; set; } = 3;
    /// <summary>
    /// Apps (process names, e.g. "WINWORD", "EXCEL", "Code") whose window text Vex may read when they're in front:
    /// sent with chat messages and used for his questions. Empty = never. Exclusions and password fields still apply.
    /// </summary>
    public List<string> WatchContentApps { get; set; } = new();
    public int WatchContentChars { get; set; } = 3000;

    // Going home: a pack-up nudge with a traffic check.
    /// <summary>When you leave work (HH:mm). Empty = no pack-up nudge.</summary>
    public string LeaveAt { get; set; } = "";
    public double PackUpMinutes { get; set; } = 15;
    public List<string> CommuteDays { get; set; } = new() { "Mon", "Tue", "Wed", "Thu", "Fri" };
    /// <summary>Addresses or postcodes. Sent to TomTom (if a key is set) to find the route; otherwise used for a Google Maps link.</summary>
    public string WorkAddress { get; set; } = "";
    public string HomeAddress { get; set; } = "";
    /// <summary>Free key from developer.tomtom.com. Empty = no live traffic, just a "Check traffic" maps link.</summary>
    public string TomTomKey { get; set; } = "";
    /// <summary>Country code(s) for looking up the addresses (bare postcodes can match abroad otherwise).</summary>
    public string TrafficCountry { get; set; } = "GB";
    /// <summary>Delay (minutes over the usual) at which he suggests leaving early.</summary>
    public double TrafficSlowMinutes { get; set; } = 10;

    // Integrations.
    public int HookPort { get; set; } = 47321;
    public string? CalendarJsonPath { get; set; }           // Power Automate export; default %OneDriveCommercial%\Vex\calendar.json
    public double ClaudeRenudgeMinutes { get; set; } = 5;
    /// <summary>
    /// Copy open journal to-dos onto the Lab Notebook's To-Do list and keep ticks in step both ways (via its inbox).
    /// Off = the To-Do list is left alone, as before.
    /// </summary>
    public bool TodoListMirror { get; set; } = true;
    /// <summary>A soft knock sound when Vex knocks on the screen for a nudge (never during calls).</summary>
    public bool KnockSound { get; set; } = true;
    public bool DebugEndpoints { get; set; } = false;

    // Workshop: Vex changing his own code when asked in chat. Off unless SourcePath is a git checkout of Vex.
    /// <summary>Vex's source repo (the folder with build.ps1). Null = no workshop.</summary>
    public string? SourcePath { get; set; }
    public string WorkshopModel { get; set; } = "opus";         // writes the code
    public string WorkshopReviewModel { get; set; } = "opus";   // checks it before commit
    /// <summary>Push to origin after an approved commit.</summary>
    public bool WorkshopPush { get; set; } = true;
    public double WorkshopTimeoutMinutes { get; set; } = 45;
    /// <summary>Full-screen apps that are just work (remote desktops, VMs): don't hide or go quiet for these.</summary>
    public List<string> FullScreenIsWork { get; set; } = new() { "mstsc", "msrdc", "vmconnect", "CDViewer", "wfica32", "vmware", "VirtualBoxVM" };

    // Privacy: never recorded, never sent anywhere. Matched case-insensitively.
    public List<string> ExcludedApps { get; set; } = new()
    {
        "KeePass", "KeePassXC", "Bitwarden", "1Password", "LastPass", "Dashlane", "NordPass",
        "CredentialUIBroker", "LogonUI", "consent",
    };
    public List<string> ExcludedTitleWords { get; set; } = new()
    {
        "password", "passcode", "sign in", "sign-in", "log in", "login", "credential", "authenticator",
        "one-time", "2fa", "bank", "payroll", "inprivate", "incognito", "private browsing",
    };
}

public class NudgeStat
{
    public int Acted { get; set; }
    public int Dismissed { get; set; }
    public int Ignored { get; set; }
    /// <summary>Multiplies the nudge's interval: drifts up when brushed off, back down when acted on.</summary>
    public double Factor { get; set; } = 1;
}

public class Reminder
{
    public DateTime At { get; set; }
    public string Text { get; set; } = "";
}

/// <summary>Day-to-day state that survives restarts.</summary>
public class VexState
{
    public string Date { get; set; } = "";
    public int Coffees { get; set; }
    public DateTime RefilledAt { get; set; } = DateTime.MinValue;
    /// <summary>
    /// For each of today's refills (newest last), when the mug had been filled before it, so "Undo last refill" on the
    /// mug's menu can put the level back as it was. Cleared with the count each day.
    /// </summary>
    public List<DateTime> RefillUndo { get; set; } = new();
    public string? ChatSessionId { get; set; }
    /// <summary>Fingerprint of the system prompt the chat session started with; a change starts a new session.</summary>
    public string? ChatPersonaHash { get; set; }
    public Dictionary<string, NudgeStat> NudgeStats { get; set; } = new();
    public List<Reminder> Reminders { get; set; } = new();
    /// <summary>Once-a-day nudges already given today (type names).</summary>
    public List<string> DoneToday { get; set; } = new();
    /// <summary>The last break or lunch you confirmed (nudge button or chat), and which: "break" or "lunch". Never guessed.</summary>
    public DateTime LastBreakAt { get; set; } = DateTime.MinValue;
    public string LastBreakKind { get; set; } = "";
    /// <summary>Old memory files/pages have been gathered into the memory folder (Services/Memory.cs), once.</summary>
    public bool MemoryAdopted { get; set; }
    /// <summary>The day the chat session started; a new day starts a new session (yesterday's is in the diary).</summary>
    public string? ChatSessionDate { get; set; }
    /// <summary>Questions Vex has asked by himself (newest last), so he doesn't ask the same thing twice.</summary>
    public List<string> AskedQuestions { get; set; } = new();
    public int AskedToday { get; set; }
    /// <summary>Address → "lat,lon|type/entityType" from TomTom, so addresses are looked up once.</summary>
    public Dictionary<string, string> GeoCache { get; set; } = new();
    /// <summary>Nudges and Build it / Restart cards that went unanswered, for the chat's Pending tab (Services/Pending.cs).</summary>
    public List<Services.PendingItem> Pending { get; set; } = new();
    /// <summary>Journal checkboxes paired with items on the Lab Notebook's To-Do list (Services/Notebook.cs, TodoListMirror).</summary>
    public List<Services.TodoLink> TodoLinks { get; set; } = new();
}

static class Store
{
    /// <summary>%LOCALAPPDATA%\Vex, or VEX_DATA_DIR if set (testing a fresh install side by side).</summary>
    public static readonly string Dir = Environment.GetEnvironmentVariable("VEX_DATA_DIR") is { Length: > 0 } d ? d : Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Vex");
    static readonly string ConfigPath = Path.Combine(Dir, "config.json");
    static readonly string StatePath = Path.Combine(Dir, "state.json");
    static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static VexConfig Config { get; } = Load<VexConfig>(ConfigPath, writeDefaults: true);

    /// <summary>Rewrites config.json so settings added in newer versions show up for editing.</summary>
    public static void SaveConfig() => Write(ConfigPath, Config);
    public static VexState State { get; } = Load<VexState>(StatePath, writeDefaults: false);

    public static string Today => DateTime.Now.ToString("yyyy-MM-dd");

    /// <summary>Resets daily counters when the date changes.</summary>
    public static void RollDay()
    {
        if (State.Date == Today) return;
        State.Date = Today;
        State.Coffees = 0;
        State.RefillUndo.Clear();
        State.DoneToday.Clear();
        State.AskedToday = 0;
        SaveState();
    }

    public static void SaveState() => Write(StatePath, State);

    public static void Log(string what)
    {
        try { File.AppendAllText(Path.Combine(Dir, "vex.log"), $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  {what}{Environment.NewLine}"); }
        catch { }
    }

    static T Load<T>(string path, bool writeDefaults) where T : new()
    {
        try
        {
            if (File.Exists(path)) return JsonSerializer.Deserialize<T>(File.ReadAllText(path)) ?? new T();
        }
        catch (Exception ex) { Log($"Couldn't read {path}: {ex.Message}"); }
        var fresh = new T();
        if (writeDefaults) Write(path, fresh);
        return fresh;
    }

    static void Write<T>(string path, T value)
    {
        try
        {
            Directory.CreateDirectory(Dir);
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(value, Json));
            File.Move(tmp, path, overwrite: true);
        }
        catch (Exception ex) { Log($"Couldn't write {path}: {ex.Message}"); }
    }
}
