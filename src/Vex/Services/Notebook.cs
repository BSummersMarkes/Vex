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
/// A journal checkbox paired with an item on the notebook's To-Do list (one Vex copied there, or one that was already
/// there with the same words), so ticking either ticks the other. Kept in state.json while the checkbox is in the
/// 14-day window, which is also what stops a restart from copying the same item twice.
/// </summary>
public class TodoLink
{
    /// <summary>The journal day the checkbox is in, and its text.</summary>
    public string Date { get; set; } = "";
    public string Text { get; set; } = "";
    /// <summary>The To-Do list item, once known (from the ack or found by its text).</summary>
    public string? TodoId { get; set; }
    /// <summary>The todo.add request, until the item shows up. The notebook may be closed for days; it's never re-sent meanwhile.</summary>
    public string? AddId { get; set; }
    /// <summary>The tick sent for this pair (either way). Set once and never re-sent, even after a restart or a refusal.</summary>
    public string? TickId { get; set; }
    /// <summary>Deleted in the notebook (or the copy was refused): leave it be and don't add it again.</summary>
    public bool Gone { get; set; }
}

/// <summary>
/// Lab Notebook integration. Reads the notebook's data file (read-only) and sends changes through
/// its inbox file, which the notebook applies and acknowledges. Contract: SPEC.md section 8.
/// </summary>
public class Notebook : INotes
{
    static readonly string SettingsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LabNotebook", "settings.json");

    public string? DataPath { get; private set; }
    public List<Todo> Todos { get; private set; } = new();
    public string TodayJournal { get; private set; } = "";
    public List<Page> Pages { get; private set; } = new();
    /// <summary>Open journal checkboxes from the last 14 days, newest first.</summary>
    public List<Check> OpenChecks { get; private set; } = new();

    /// <summary>The notebook project that mirrors Vex's memory folder (the notebook does the mirroring).</summary>
    public const string VexProject = "Vex";

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
    public string Name => "Lab Notebook";
    public bool Live => Running;

    /// <summary>The notebook's data file, if the Lab Notebook is installed and linked on this PC.</summary>
    public static string? Detect() => ReadDataPath() is { } p && File.Exists(p) ? p : null;

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
            var ticked = new List<Check>();   // only needed to tell "ticked" from "edited away" when mirroring
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
                        else if (!fenced && INotes.AnyCheckRx.Match(line) is { Success: true } x) ticked.Add(new Check(date, x.Groups["text"].Value.Trim()));
                    }
                }
            OpenChecks = checks.OrderByDescending(c => c.Date).ToList();

            var pages = new List<Page>();
            if (root.TryGetProperty("notes", out var ns) && ns.ValueKind == JsonValueKind.Array)
                foreach (var n in ns.EnumerateArray())
                    pages.Add(new Page(Str(n, "id") ?? "", Str(n, "title") ?? "", Str(n, "body") ?? "", Str(n, "project") ?? ""));
            Pages = pages;

            Mirror(checks, ticked, since);
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

    /// <summary>Open items on the To-Do list, minus the copies of journal checkboxes (those are already in <see cref="OpenChecks"/>).</summary>
    public IEnumerable<Todo> Open => Todos.Where(t => !t.Done && !Store.State.TodoLinks.Any(l => l.TodoId == t.Id));

    // ───────────── mirroring journal to-dos onto the To-Do list ─────────────
    // The journal checkboxes are the real to-dos; the notebook's To-Do list gets a copy of each open one so Ben sees
    // the same items in both places. Ticking either side ticks the other. Everything goes through the inbox like any
    // other change, and existing To-Do items are never edited except to tick a copy Vex made (or one that matches).

    static string Norm(string s) => System.Text.RegularExpressions.Regex.Replace(s.Trim(), @"\s+", " ").ToLowerInvariant();
    static bool Same(Check c, TodoLink l) => c.Date == l.Date && Norm(c.Text) == Norm(l.Text);

    /// <summary>Brings the To-Do list in line with the journal checkboxes (last 14 days). Runs after every load.</summary>
    void Mirror(List<Check> open, List<Check> ticked, string since)
    {
        if (!Store.Config.TodoListMirror) return;
        var links = Store.State.TodoLinks;
        // Older than the window: both sides stay as they are, Vex just stops looking after them.
        bool changed = links.RemoveAll(l => string.CompareOrdinal(l.Date, since) < 0) > 0;
        var claimed = links.Where(l => l.TodoId != null).Select(l => l.TodoId!).ToHashSet();

        foreach (var l in links.ToList())
        {
            bool isOpen = open.Any(c => Same(c, l));
            // Reworded or removed in the journal: stop tracking, and leave the To-Do item alone rather than guess.
            // Its To-Do item is freed too, so a checkbox moved to another day pairs with it again instead of adding a duplicate.
            if (!isOpen && !ticked.Any(c => Same(c, l)))
            {
                links.Remove(l); changed = true;
                if (l.TodoId != null) claimed.Remove(l.TodoId);
                continue;
            }
            if (l.Gone) continue;

            var todo = Todos.FirstOrDefault(t => t.Id == l.TodoId);
            // The ack may not carry the new item's id, so look for it by text (open ones only, so an old finished
            // item with the same words isn't mistaken for it).
            if (todo == null && l.AddId != null)
                todo = Todos.FirstOrDefault(t => !t.Done && !claimed.Contains(t.Id) && Norm(t.Text) == Norm(l.Text));
            if (todo == null)
            {
                // Seen before and now missing: Ben deleted it in the notebook, so don't put it back.
                if (l.AddId == null) { l.Gone = true; changed = true; }
                continue;   // otherwise the add is still waiting for the notebook to apply it
            }
            if (l.TodoId != todo.Id || l.AddId != null) { l.TodoId = todo.Id; l.AddId = null; claimed.Add(todo.Id); changed = true; }

            // One tick per pair, ever: unticking isn't mirrored, so a later mismatch means Ben unticked one side (leave it),
            // or the notebook refused or ignored the tick (logged by Quiet). Re-sending would undo him or loop on every load.
            if (l.TickId != null) continue;
            if (!isOpen && !todo.Done)
                { l.TickId = Quiet(new { cmd = "todo.done", todoId = todo.Id, source = "vex" }, $"tick To-Do copy of \"{l.Text}\""); changed = true; }
            else if (isOpen && todo.Done)
                { l.TickId = Quiet(new { cmd = "journal.done", text = l.Text, date = l.Date, source = "vex" }, $"tick journal \"{l.Text}\""); changed = true; }
        }

        foreach (var c in open)
        {
            if (links.Any(l => Same(c, l))) continue;
            var link = new TodoLink { Date = c.Date, Text = c.Text };
            // Already on the To-Do list (typed in both, or there from before): pair them up instead of adding a duplicate.
            if (Todos.FirstOrDefault(t => !t.Done && !claimed.Contains(t.Id) && Norm(t.Text) == Norm(c.Text)) is { } same)
            {
                link.TodoId = same.Id;
                claimed.Add(same.Id);
            }
            else
            {
                link.AddId = Quiet(new { cmd = "todo.add", text = c.Text, source = "vex" }, $"copy \"{c.Text}\" to the To-Do list", (ok, r) =>
                {
                    if (ok) { if (Str(r, "todoId") is { } id) link.TodoId = id; return; }
                    // Rejected (e.g. the notebook doesn't take todo.add): give up on this one rather than retry on every load.
                    link.Gone = true; link.AddId = null; Store.SaveState();
                });
                if (link.AddId == null) continue;   // inbox busy; try again next load
            }
            links.Add(link);
            changed = true;
        }
        if (changed) Store.SaveState();
    }

    /// <summary>Sends a mirroring request without a "Noted." bubble: it's housekeeping Ben didn't ask for. Failures are only logged.</summary>
    string? Quiet(object body, string description, Action<bool, JsonElement>? then = null) =>
        Send(body, description, (ok, err, r) =>
        {
            if (!ok) Store.Log($"Notebook to-do mirror, couldn't {description}: {err ?? "unknown error"}");
            then?.Invoke(ok, r);
        });

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

    public string? AppendJournal(string text, bool quiet = false) =>
        Send(new { cmd = "journal.append", text, date = Store.Today, time = DateTime.Now.ToString("HH:mm"), source = "vex" },
             $"Journal: {text}",
             quiet ? (ok, err, _) => { if (!ok) Store.Log($"Notebook couldn't add to the journal: {err ?? "unknown error"}"); } : null);

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
            if (pending.Count == 0 && new FileInfo(Done).Length > 64 * 1024) File.WriteAllText(Done, "");
        }
        catch (Exception ex) { Store.Log($"Notebook acks: {ex.Message}"); }
    }

    public int Pending => pending.Count;
}
