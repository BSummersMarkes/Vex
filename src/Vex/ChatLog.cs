using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace Vex;

public record ChatLine(DateTime At, string Who, string Text);

/// <summary>
/// The visible chat transcript, one file per day (chats\YYYY-MM-DD.jsonl), kept so closing the panel or
/// restarting Vex doesn't wipe it and so old chats can be looked back on. Each day starts a fresh Claude
/// conversation; the nightly diary (Services/Diary.cs) carries the gist of earlier days forward.
/// </summary>
static class ChatLog
{
    public static readonly string Folder = Path.Combine(Store.Dir, "chats");
    public const int Show = 60;    // lines shown when the panel opens

    static string FileFor(DateTime day) => Path.Combine(Folder, $"{day:yyyy-MM-dd}.jsonl");

    /// <summary>Splits the old single chat.jsonl (before per-day files) into days, once.</summary>
    public static void Start()
    {
        var old = Path.Combine(Store.Dir, "chat.jsonl");
        try
        {
            Directory.CreateDirectory(Folder);
            // Renamed first, so a failure part-way can't append the same lines twice on the next start.
            var moving = old + ".splitting";
            if (File.Exists(old)) File.Move(old, moving, overwrite: true);
            if (!File.Exists(moving)) return;
            foreach (var day in Parse(File.ReadAllLines(moving)).GroupBy(l => l.At.Date))
                File.WriteAllLines(FileFor(day.Key), day.Select(l => JsonSerializer.Serialize(l)));
            File.Delete(moving);
        }
        catch (Exception ex) { Store.Log($"Chat log split: {ex.Message}"); }
    }

    public static void Append(string who, string text)
    {
        try
        {
            Directory.CreateDirectory(Folder);
            File.AppendAllText(FileFor(DateTime.Today), JsonSerializer.Serialize(new ChatLine(DateTime.Now, who, text)) + Environment.NewLine);
        }
        catch (Exception ex) { Store.Log($"Chat log: {ex.Message}"); }
    }

    /// <summary>One day's chat.</summary>
    public static List<ChatLine> Day(DateTime day)
    {
        try { return File.Exists(FileFor(day)) ? Parse(File.ReadAllLines(FileFor(day))).ToList() : new(); }
        catch (Exception ex) { Store.Log($"Chat log: {ex.Message}"); return new(); }
    }

    /// <summary>The last <paramref name="n"/> lines, reaching back into earlier days' files if today is quiet.</summary>
    public static List<ChatLine> Recent(int n = Show)
    {
        var lines = new List<ChatLine>();
        foreach (var day in Days().OrderByDescending(d => d).Take(7))
        {
            lines.InsertRange(0, Day(day));
            if (lines.Count >= n) break;
        }
        return lines.TakeLast(n).ToList();
    }

    /// <summary>Days that have a chat file.</summary>
    public static IEnumerable<DateTime> Days()
    {
        if (!Directory.Exists(Folder)) yield break;
        foreach (var f in Directory.GetFiles(Folder, "*.jsonl"))
            if (DateTime.TryParseExact(Path.GetFileNameWithoutExtension(f), "yyyy-MM-dd", null, System.Globalization.DateTimeStyles.None, out var d))
                yield return d;
    }

    /// <summary>"Forget today's chat": removes today's file. Earlier days stay in the archive (and the diary).</summary>
    public static void ClearToday()
    {
        try { File.Delete(FileFor(DateTime.Today)); } catch { }
    }

    static IEnumerable<ChatLine> Parse(IEnumerable<string> lines)
    {
        foreach (var l in lines)
        {
            ChatLine? line = null;
            try { line = JsonSerializer.Deserialize<ChatLine>(l); } catch { }
            if (line != null) yield return line;
        }
    }
}
