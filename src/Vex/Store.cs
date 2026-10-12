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

    /// <summary>Keep Vex (and his chat) out of screenshots and screen shares.</summary>
    public bool HideFromScreenCapture { get; set; } = true;

    // Presence and nudges (minutes unless noted).
    public double BreakMinutes { get; set; } = 10;          // idle this long (or locked 3+ min) counts as a break
    public double NudgeGapMinutes { get; set; } = 15;       // at most one wellbeing nudge per gap
    public double EyesEveryMinutes { get; set; } = 20;
    public double StretchEveryMinutes { get; set; } = 55;
    public double WaterEveryMinutes { get; set; } = 120;
    public double FocusCheckMinutes { get; set; } = 120;
    public string LunchBy { get; set; } = "13:30";
    public string EndOfDay { get; set; } = "17:15";
    public int[] MeetingWarnMinutes { get; set; } = { 10, 2 };

    // Integrations.
    public int HookPort { get; set; } = 47321;
    public string? CalendarJsonPath { get; set; }           // Power Automate export; default %OneDriveCommercial%\Vex\calendar.json
    public double ClaudeRenudgeMinutes { get; set; } = 5;
    /// <summary>A soft knock sound when Vex knocks on the screen for a nudge (never during calls).</summary>
    public bool KnockSound { get; set; } = true;
    public bool DebugEndpoints { get; set; } = false;
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
    public string? ChatSessionId { get; set; }
    public Dictionary<string, NudgeStat> NudgeStats { get; set; } = new();
    public List<Reminder> Reminders { get; set; } = new();
    /// <summary>Once-a-day nudges already given today (type names).</summary>
    public List<string> DoneToday { get; set; } = new();
    /// <summary>Local memory files have been moved into the notebook's Vex project.</summary>
    public bool MemoryMigrated { get; set; }
    public List<string> MigratedPages { get; set; } = new();
    /// <summary>Outstanding migration request id per page (survives restarts, so nothing is sent twice).</summary>
    public Dictionary<string, string> MigrationIds { get; set; } = new();
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
        State.DoneToday.Clear();
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
