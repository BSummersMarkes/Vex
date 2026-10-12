using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace Vex;

public record ChatLine(DateTime At, string Who, string Text);

/// <summary>
/// The visible chat transcript, kept on disk so closing the panel (or restarting Vex) doesn't wipe it.
/// Claude keeps its own copy of the conversation via the resumed session; this is just what you see.
/// </summary>
static class ChatLog
{
    static readonly string Path = System.IO.Path.Combine(Store.Dir, "chat.jsonl");
    const int Keep = 400;   // lines kept on disk
    public const int Show = 60;    // lines shown when the panel opens

    public static void Append(string who, string text)
    {
        try
        {
            File.AppendAllText(Path, JsonSerializer.Serialize(new ChatLine(DateTime.Now, who, text)) + Environment.NewLine);
        }
        catch (Exception ex) { Store.Log($"Chat log: {ex.Message}"); }
    }

    public static List<ChatLine> Recent(int n = Show)
    {
        try
        {
            if (!File.Exists(Path)) return new();
            var lines = File.ReadAllLines(Path);
            if (lines.Length > Keep) File.WriteAllLines(Path, lines[^Keep..]); // trim now and then
            return lines.TakeLast(n).Select(l => JsonSerializer.Deserialize<ChatLine>(l)).OfType<ChatLine>().ToList();
        }
        catch (Exception ex) { Store.Log($"Chat log: {ex.Message}"); return new(); }
    }

    public static void Clear()
    {
        try { File.Delete(Path); } catch { }
    }
}
