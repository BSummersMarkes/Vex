using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Windows.Forms;
using Microsoft.Win32;

namespace Vex.Services;

public record Span(DateTime Start, double Secs, string App, string Title)
{
    public DateTime End => Start.AddSeconds(Secs);
}

/// <summary>
/// A timeline of which app and window title were in front, sampled every few seconds.
/// Exclusions are applied before anything is kept, so private windows never reach memory, disk or Claude.
/// Also answers "is Ben in a call?" and "is something full screen?".
/// </summary>
public class Activity
{
    public const string Private = "(private)", Away = "(away)";

    public List<Span> Today { get; } = new();
    public string CurrentApp { get; private set; } = "";
    public string CurrentTitle { get; private set; } = "";
    public DateTime CurrentSince { get; private set; } = DateTime.Now;

    readonly uint ownPid = (uint)Environment.ProcessId;
    readonly Dictionary<uint, string> names = new();
    string day = Store.Today;

    static string LogPath(string day) => Path.Combine(Store.Dir, "log", $"activity-{day}.jsonl");

    public Activity() => LoadToday();

    public void Tick(bool away)
    {
        if (day != Store.Today) { Close(); day = Store.Today; Today.Clear(); }

        string app, title;
        if (away) { app = Away; title = ""; }
        else
        {
            var h = Native.GetForegroundWindow();
            if (h == IntPtr.Zero) return;
            Native.GetWindowThreadProcessId(h, out uint pid);
            if (pid == ownPid) return; // talking to Vex doesn't change what you're working on
            app = ProcessName(pid);
            title = Native.WindowText(h).Trim();
            if (IsPrivate(app, title)) { app = Private; title = ""; }
            if (title.Length > 140) title = title[..140] + "…";
        }

        if (app == CurrentApp && title == CurrentTitle) return;
        Close();
        CurrentApp = app; CurrentTitle = title; CurrentSince = DateTime.Now;
    }

    void Close()
    {
        if (CurrentApp == "") return;
        var span = new Span(CurrentSince, (DateTime.Now - CurrentSince).TotalSeconds, CurrentApp, CurrentTitle);
        if (span.Secs < 2) return;
        Today.Add(span);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(LogPath(day))!);
            File.AppendAllText(LogPath(day), JsonSerializer.Serialize(span) + Environment.NewLine);
        }
        catch (Exception ex) { Store.Log($"Activity log: {ex.Message}"); }
    }

    public void Flush() { Close(); CurrentSince = DateTime.Now; }

    void LoadToday()
    {
        try
        {
            if (!File.Exists(LogPath(day))) return;
            foreach (var line in File.ReadLines(LogPath(day)))
                if (JsonSerializer.Deserialize<Span>(line) is { } s) Today.Add(s);
        }
        catch (Exception ex) { Store.Log($"Activity load: {ex.Message}"); }
    }

    static bool IsPrivate(string app, string title)
    {
        var c = Store.Config;
        if (c.ExcludedApps.Any(a => a.Equals(app, StringComparison.OrdinalIgnoreCase))) return true;
        return c.ExcludedTitleWords.Any(w => title.Contains(w, StringComparison.OrdinalIgnoreCase));
    }

    string ProcessName(uint pid)
    {
        if (names.TryGetValue(pid, out var n)) return n;
        try { n = Process.GetProcessById((int)pid).ProcessName; } catch { n = "?"; }
        if (names.Count > 500) names.Clear();
        return names[pid] = n;
    }

    /// <summary>Spans (including the current one) overlapping the last <paramref name="window"/>.</summary>
    public IEnumerable<Span> Since(DateTime from)
    {
        foreach (var s in Today)
            if (s.End > from) yield return s.Start < from ? s with { Start = from, Secs = (s.End - from).TotalSeconds } : s;
        if (CurrentApp != "")
        {
            var start = CurrentSince < from ? from : CurrentSince;
            yield return new Span(start, (DateTime.Now - start).TotalSeconds, CurrentApp, CurrentTitle);
        }
    }

    public List<(string App, double Minutes)> TopApps(DateTime from, int n = 6) =>
        Since(from).Where(s => s.App != Away)
            .GroupBy(s => s.App).Select(g => (g.Key, g.Sum(s => s.Secs) / 60))
            .OrderByDescending(x => x.Item2).Take(n).ToList();

    public double MinutesIn(string app, DateTime from) =>
        Since(from).Where(s => s.App == app).Sum(s => s.Secs) / 60;

    /// <summary>The last few distinct things worked on, newest first.</summary>
    public List<Span> RecentWork(int n = 8) =>
        Since(DateTime.Now.AddHours(-3)).Reverse()
            .Where(s => s.App is not (Away or Private) && s.Secs >= 20)
            .DistinctBy(s => s.App + s.Title).Take(n).ToList();

    // ───────────── calls and full screen ─────────────

    /// <summary>Name of an app currently using the microphone (a decent proxy for "in a call"), or null.</summary>
    public static string? MicInUse()
    {
        const string root = @"Software\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\microphone";
        try
        {
            using var mic = Registry.CurrentUser.OpenSubKey(root);
            if (mic == null) return null;
            foreach (var (key, name) in Apps(mic))
                if (key.GetValue("LastUsedTimeStop") is long stop && stop == 0
                    && key.GetValue("LastUsedTimeStart") is long start && start > 0)
                    return name;
        }
        catch { }
        return null;

        static IEnumerable<(RegistryKey, string)> Apps(RegistryKey mic)
        {
            foreach (var sub in mic.GetSubKeyNames())
            {
                using var k = mic.OpenSubKey(sub);
                if (k == null) continue;
                if (sub == "NonPackaged")
                {
                    foreach (var exe in k.GetSubKeyNames())
                    {
                        using var e = k.OpenSubKey(exe);
                        if (e != null) yield return (e, Path.GetFileNameWithoutExtension(exe.Split('#').Last()));
                    }
                }
                else yield return (k, sub.Split('_')[0]);
            }
        }
    }

    /// <summary>True when the foreground window covers a whole monitor (presentations, full-screen video).</summary>
    public static bool FullScreen()
    {
        var h = Native.GetForegroundWindow();
        if (h == IntPtr.Zero) return false;
        var cls = Native.ClassName(h);
        if (cls is "Progman" or "WorkerW" or "Shell_TrayWnd") return false;
        Native.GetWindowThreadProcessId(h, out uint pid);
        try
        {
            var name = Process.GetProcessById((int)pid).ProcessName;
            if (pid == Environment.ProcessId || Store.Config.FullScreenIsWork.Any(a => a.Equals(name, StringComparison.OrdinalIgnoreCase))) return false;
        }
        catch { }
        Native.GetWindowRect(h, out var r);
        foreach (var s in Screen.AllScreens)
            if (r.Left <= s.Bounds.Left && r.Top <= s.Bounds.Top && r.Right >= s.Bounds.Right && r.Bottom >= s.Bounds.Bottom)
                return true;
        return false;
    }
}
