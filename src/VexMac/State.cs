using System;
using System.IO;
using System.Text.Json;

namespace VexMac;

/// <summary>What survives a restart: today's coffees, the mug and the last confirmed break. Kept in DataDir/state.json.</summary>
public class State
{
    public string Day { get; set; } = "";
    public int Coffees { get; set; }
    public DateTime RefilledAt { get; set; } = DateTime.MinValue;
    /// <summary>Start of the sitting timer: the last break you confirmed, or when you were first seen today.</summary>
    public DateTime SittingSince { get; set; } = DateTime.Now;
    public DateTime LastWater { get; set; } = DateTime.Now;

    static string PathOf(string name) => Path.Combine(Platform.DataDir, name);
    static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    public static State Current { get; } = Load();

    static State Load()
    {
        try
        {
            if (File.Exists(PathOf("state.json")))
                return JsonSerializer.Deserialize<State>(File.ReadAllText(PathOf("state.json"))) ?? new State();
        }
        catch (Exception ex) { Log($"Couldn't read state: {ex.Message}"); }
        return new State();
    }

    /// <summary>A new day: fresh coffee count and sitting timer.</summary>
    public void RollDay()
    {
        var today = DateTime.Today.ToString("yyyy-MM-dd");
        if (Day == today) return;
        Day = today; Coffees = 0; SittingSince = DateTime.Now; LastWater = DateTime.Now;
        Save();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Platform.DataDir);
            File.WriteAllText(PathOf("state.json"), JsonSerializer.Serialize(this, Indented));
        }
        catch (Exception ex) { Log($"Couldn't save state: {ex.Message}"); }
    }

    public static void Log(string line)
    {
        try
        {
            Directory.CreateDirectory(Platform.DataDir);
            File.AppendAllText(PathOf("vex.log"), $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  {line}{Environment.NewLine}");
        }
        catch { }
    }
}
