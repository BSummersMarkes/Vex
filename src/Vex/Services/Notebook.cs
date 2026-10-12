using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Windows.Threading;

namespace Vex.Services;

public record Todo(string Id, string Text, bool Done, string? Due, string Project);
public record Page(string Id, string Title, string Body, string Project);
/// <summary>An unticked "- [ ]" line in a journal entry.</summary>
public record Check(string Date, string Text);

/// <summary>
/// Lab Notebook integration. Reads the notebook's data file (read-only) and sends changes through
/// its inbox file, which the notebook applies and acknowledges. Contract: SPEC.md section 8.
/// </summary>
public class Notebook
{
    static readonly string SettingsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LabNotebook", "settings.json");

    public string? DataPath { get; private set; }
    public List<Todo> Todos { get; private set; } = new();
    public string TodayJournal { get; private set; } = "";
    public List<Page> Pages { get; private set; } = new();
    /// <summary>Open journal checkboxes from the last 14 days, newest first.</summary>
    public List<Check> OpenChecks { get; private set; } = new();

    public const string VexProject = "Vex";
    public IEnumerable<Page> VexPages => Pages.Where(p => p.Project.Equals(VexProject, StringComparison.OrdinalIgnoreCase));

    static readonly System.Text.RegularExpressions.Regex OpenCheckRx =
        new(@"^(\s*>)*\s*([-*+]|\d+[.)])\s+\[ \]\s+(?<text>.+)$");

    /// <summary>Fired on the UI thread with (description, ok, error) when the notebook acknowledges a request.</summary>
    public event Action<string, bool, string?>? Acked;

    readonly Dispatcher ui = Dispatcher.CurrentDispatcher;
    record Pend(string Description, Action<bool, string?, JsonElement>? OnAck);
    readonly Dictionary<string, Pend> pending = new();
    readonly HashSet<string> seenAcks = new();
    FileSystemWatcher? watcher;
    DispatcherTimer? debounce;

    string Inbox => Path.ChangeExtension(DataPath!, ".inbox.jsonl");
    string Done => Path.ChangeExtension(DataPath!, ".inbox.done.jsonl");

    public bool Linked => DataPath != null && File.Exists(DataPath);
    public static bool Running => Process.GetProcessesByName("LabNotebook").Length > 0;

    public void Start()
    {
        DataPath = ReadDataPath();
        Load();
        ReadAcks();
        Watch();
    }

    static string? ReadDataPath()
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(SettingsPath));
            return doc.RootElement.TryGetProperty("DataPath", out var p) ? p.GetString() : null;
        }
        catch { return null; }
    }

    void Watch()
    {
        watcher?.Dispose(); watcher = null;
        if (!Linked) return;
        watcher = new FileSystemWatcher(Path.GetDirectoryName(DataPath!)!, Path.GetFileNameWithoutExtension(DataPath!) + "*")
        {
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
        };
        FileSystemEventHandler changed = (_, _) => ui.BeginInvoke(Debounced);
        watcher.Changed += changed; watcher.Created += changed;
        watcher.Renamed += (_, _) => ui.BeginInvoke(Debounced);
        watcher.EnableRaisingEvents = true;
    }

    void Debounced()
    {
        debounce ??= new DispatcherTimer(TimeSpan.FromMilliseconds(400), DispatcherPriority.Background, (_, _) =>
        {
            debounce!.Stop();
            Load(); ReadAcks();
        }, ui);
        debounce.Stop(); debounce.Start();
    }

    /// <summary>Re-reads settings (in case the notebook was relinked) and the data file.</summary>
    public void Load()
    {
        var path = ReadDataPath();
        if (path != DataPath) { DataPath = path; Watch(); }
        if (!Linked) return;
        try
        {
            using var doc = JsonDocument.Parse(ReadShared(DataPath!));
            var root = doc.RootElement;
            var todos = new List<Todo>();
            if (root.TryGetProperty("todos", out var ts) && ts.ValueKind == JsonValueKind.Array)
                foreach (var t in ts.EnumerateArray())
                    todos.Add(new Todo(Str(t, "id") ?? "", Str(t, "text") ?? "",
                        t.TryGetProperty("done", out var d) && d.ValueKind == JsonValueKind.True,
                        Str(t, "due"), Str(t, "project") ?? ""));
            Todos = todos;

            TodayJournal = "";
            var checks = new List<Check>();
            var since = DateTime.Today.AddDays(-14).ToString("yyyy-MM-dd");
            if (root.TryGetProperty("journal", out var js) && js.ValueKind == JsonValueKind.Array)
                foreach (var j in js.EnumerateArray())
                {
                    var date = Str(j, "date") ?? "";
                    var body = Str(j, "body") ?? "";
                    if (date == Store.Today) TodayJournal = body;
                    if (string.CompareOrdinal(date, since) < 0) continue;
                    bool fenced = false;
                    foreach (var raw in body.Split('\n'))
                    {
                        var line = raw.TrimEnd((char)13);
                        if (line.TrimStart().StartsWith("```")) { fenced = !fenced; continue; }
                        if (!fenced && OpenCheckRx.Match(line) is { Success: true } m) checks.Add(new Check(date, m.Groups["text"].Value.Trim()));
                    }
                }
            OpenChecks = checks.OrderByDescending(c => c.Date).ToList();

            var pages = new List<Page>();
            if (root.TryGetProperty("notes", out var ns) && ns.ValueKind == JsonValueKind.Array)
                foreach (var n in ns.EnumerateArray())
                    pages.Add(new Page(Str(n, "id") ?? "", Str(n, "title") ?? "", Str(n, "body") ?? "", Str(n, "project") ?? ""));
            Pages = pages;
        }
        catch (Exception ex) { Store.Log($"Notebook load: {ex.Message}"); }
    }

    static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    /// <summary>Reads without blocking the notebook's save (which renames over the file).</summary>
    static string ReadShared(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var sr = new StreamReader(fs, Encoding.UTF8);
        return sr.ReadToEnd();
    }

    public IEnumerable<Todo> Open => Todos.Where(t => !t.Done);

    // ───────────── writing (via the inbox) ─────────────

    /// <summary>Adds "- [ ] text" to today's journal entry.</summary>
    public string? JournalTodo(string text) =>
        Send(new { cmd = "journal.todo", text, source = "vex" }, $"To-do: {text}");

    /// <summary>
    /// Ticks the newest matching open journal checkbox (last 14 days). Falls back to the old To-Do list
    /// if there's no such checkbox, so items added before the switch can still be ticked off.
    /// </summary>
    public string? JournalDone(string text) =>
        Send(new { cmd = "journal.done", text, source = "vex" }, $"Ticked off: {text}", (ok, err, r) =>
        {
            if (ok) { Acked?.Invoke($"Ticked off: {Str(r, "text") ?? text}", true, null); return; }
            if (CompleteTodo(text) == null) Acked?.Invoke($"Ticked off: {text}", false, err);
        });

    public string? AppendJournal(string text) =>
        Send(new { cmd = "journal.append", text, date = Store.Today, time = DateTime.Now.ToString("HH:mm"), source = "vex" },
             $"Journal: {text}");

    /// <summary>Appends markdown to a page in the Vex project (both created if missing).</summary>
    public string? PageAppend(string page, string text, Action<bool, string?, JsonElement>? onAck = null) =>
        Send(new { cmd = "page.append", project = VexProject, page, text, source = "vex" }, $"Added to \"{page}\"", onAck);

    // The old To-Do list: still read, and used as a fallback for ticking things off.
    public string? CompleteTodo(string text)
    {
        var match = Open.FirstOrDefault(t => t.Text.Trim().Equals(text.Trim(), StringComparison.OrdinalIgnoreCase))
                    ?? Open.FirstOrDefault(t => t.Text.Contains(text.Trim(), StringComparison.OrdinalIgnoreCase));
        return match == null ? null : Send(new { cmd = "todo.done", todoId = match.Id, source = "vex" }, $"Ticked off: {match.Text}");
    }

    static readonly JsonSerializerOptions NoNulls = new() { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull };

    string? Send(object body, string description, Action<bool, string?, JsonElement>? onAck = null)
    {
        if (!Linked) return null;
        var id = Guid.NewGuid().ToString();
        var json = JsonSerializer.Serialize(body, NoNulls);
        var line = "{\"id\":" + JsonSerializer.Serialize(id) + "," + json[1..] + Environment.NewLine;
        for (int attempt = 0; attempt < 10; attempt++)
        {
            try
            {
                File.AppendAllText(Inbox, line, new UTF8Encoding(false));
                pending[id] = new Pend(description, onAck);
                return id;
            }
            catch (IOException) { Thread.Sleep(60); } // the notebook is claiming the inbox; try again
        }
        Store.Log($"Notebook inbox busy, gave up: {description}");
        return null;
    }

    void ReadAcks()
    {
        if (!Linked || !File.Exists(Done)) return;
        try
        {
            foreach (var line in ReadShared(Done).Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                using var doc = JsonDocument.Parse(line);
                var r = doc.RootElement;
                var id = Str(r, "id");
                if (id == null || !seenAcks.Add(id) || !pending.Remove(id, out var p)) continue;
                bool ok = r.TryGetProperty("ok", out var o) && o.ValueKind == JsonValueKind.True;
                if (p.OnAck != null) p.OnAck(ok, Str(r, "error"), r.Clone());
                else Acked?.Invoke(p.Description, ok, Str(r, "error"));
            }
            // Keep the ack file small once nothing we're waiting for is in it.
            if (pending.Count == 0 && Store.State.MigrationIds.Count == 0 && new FileInfo(Done).Length > 64 * 1024) File.WriteAllText(Done, "");
        }
        catch (Exception ex) { Store.Log($"Notebook acks: {ex.Message}"); }
    }

    public int Pending => pending.Count;

    /// <summary>The notebook's answer to a request, or null if it hasn't been processed yet.</summary>
    public (bool Ok, string? Error)? FindAck(string id)
    {
        if (!Linked || !File.Exists(Done)) return null;
        try
        {
            foreach (var line in ReadShared(Done).Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                if (!line.Contains(id)) continue;
                using var doc = JsonDocument.Parse(line);
                var r = doc.RootElement;
                if (Str(r, "id") != id) continue;
                return (r.TryGetProperty("ok", out var o) && o.ValueKind == JsonValueKind.True, Str(r, "error"));
            }
        }
        catch (Exception ex) { Store.Log($"Notebook ack lookup: {ex.Message}"); }
        return null;
    }
}
