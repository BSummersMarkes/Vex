using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows.Threading;

namespace Vex.Services;

/// <summary>
/// Where Vex keeps to-dos, notes and his own pages. Either the Lab Notebook (if it's installed and
/// linked) or a plain folder of markdown files, for people who don't have the notebook.
/// </summary>
public interface INotes
{
    /// <summary>Human name for messages, e.g. "Lab Notebook" or "your notes folder".</summary>
    string Name { get; }
    /// <summary>Set up and readable.</summary>
    bool Linked { get; }
    /// <summary>Changes apply now (false: the Lab Notebook is closed, so they're queued).</summary>
    bool Live { get; }

    List<Check> OpenChecks { get; }
    /// <summary>The Lab Notebook's separate To-Do list (empty for local notes).</summary>
    IEnumerable<Todo> Open { get; }
    string TodayJournal { get; }
    IEnumerable<Page> VexPages { get; }

    event Action<string, bool, string?>? Acked;
    void Start();

    string? JournalTodo(string text);
    string? JournalDone(string text);
    string? AppendJournal(string text);
    string? PageAppend(string page, string text, Action<bool, string?, JsonElement>? onAck = null);
    (bool Ok, string? Error)? FindAck(string id);

    static readonly Regex OpenCheckRx = new(@"^(\s*>)*\s*([-*+]|\d+[.)])\s+\[ \]\s+(?<text>.+)$");
    static readonly Regex AnyCheckRx = new(@"^(?<pre>(\s*>)*\s*([-*+]|\d+[.)])\s+\[)(?<mark>[ xX])(?<post>\]\s+)(?<text>.+)$");
}

/// <summary>
/// Notes as ordinary markdown files, so they open in any editor:
///   {folder}\Journal\YYYY-MM-DD.md   daily log, "- [ ] item" to-dos, "[HH:mm] note" lines
///   {folder}\Vex\{Page}.md           Vex's pages (About me, Remembered, ...)
/// Same behaviour as the notebook: to-dos are ticked in place and stay in that day's file.
/// </summary>
public class LocalNotes : INotes
{
    public string Folder { get; }
    string JournalDir => Path.Combine(Folder, "Journal");
    string PagesDir => Path.Combine(Folder, "Vex");

    public LocalNotes(string folder) => Folder = folder;

    public string Name => "your notes folder";
    public bool Linked => Directory.Exists(Folder);
    public bool Live => true;
    public List<Check> OpenChecks { get; private set; } = new();
    public IEnumerable<Todo> Open => Enumerable.Empty<Todo>();
    public string TodayJournal { get; private set; } = "";
    public IEnumerable<Page> VexPages => pages;
    public event Action<string, bool, string?>? Acked;

    List<Page> pages = new();
    readonly HashSet<string> done = new();
    readonly Dispatcher ui = Dispatcher.CurrentDispatcher;
    FileSystemWatcher? watcher;
    DispatcherTimer? debounce;

    public void Start()
    {
        try
        {
            Directory.CreateDirectory(JournalDir);
            Directory.CreateDirectory(PagesDir);
        }
        catch (Exception ex) { Store.Log($"Notes folder unusable ({Folder}): {ex.Message}"); return; } // Linked stays false
        Load();
        watcher = new FileSystemWatcher(Folder, "*.md") { IncludeSubdirectories = true, NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName };
        FileSystemEventHandler changed = (_, _) => ui.BeginInvoke(Debounced);
        watcher.Changed += changed; watcher.Created += changed; watcher.Deleted += changed;
        watcher.Renamed += (_, _) => ui.BeginInvoke(Debounced);
        watcher.EnableRaisingEvents = true;
    }

    void Debounced()
    {
        debounce ??= new DispatcherTimer(TimeSpan.FromMilliseconds(400), DispatcherPriority.Background, (_, _) => { debounce!.Stop(); Load(); }, ui);
        debounce.Stop(); debounce.Start();
    }

    string DayFile(DateTime d) => Path.Combine(JournalDir, d.ToString("yyyy-MM-dd") + ".md");

    void Load()
    {
        try
        {
            var checks = new List<Check>();
            for (int back = 0; back <= 14; back++)
            {
                var d = DateTime.Today.AddDays(-back);
                var f = DayFile(d);
                if (!File.Exists(f)) continue;
                var body = File.ReadAllText(f);
                if (back == 0) TodayJournal = body;
                bool fenced = false;
                foreach (var raw in body.Split('\n'))
                {
                    var line = raw.TrimEnd((char)13);
                    if (line.TrimStart().StartsWith("```")) { fenced = !fenced; continue; }
                    if (!fenced && INotes.OpenCheckRx.Match(line) is { Success: true } m)
                        checks.Add(new Check(d.ToString("yyyy-MM-dd"), m.Groups["text"].Value.Trim()));
                }
            }
            if (!File.Exists(DayFile(DateTime.Today))) TodayJournal = "";
            OpenChecks = checks;
            pages = Directory.Exists(PagesDir)
                ? Directory.GetFiles(PagesDir, "*.md").Select(f => new Page(f, Path.GetFileNameWithoutExtension(f), File.ReadAllText(f), Notebook.VexProject)).ToList()
                : new();
        }
        catch (Exception ex) { Store.Log($"Local notes: {ex.Message}"); }
    }

    string Ok(string description, Action<bool, string?, JsonElement>? onAck = null, string? error = null)
    {
        var id = Guid.NewGuid().ToString();
        bool ok = error == null;
        if (ok) done.Add(id);
        Load();
        if (onAck != null) onAck(ok, error, default);
        else if (!ok) Acked?.Invoke(description, false, error);
        return id;
    }

    void AppendLine(string file, string line)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        var sep = File.Exists(file) && new FileInfo(file).Length > 0 && !File.ReadAllText(file).EndsWith('\n') ? Environment.NewLine : "";
        File.AppendAllText(file, sep + line + Environment.NewLine, new UTF8Encoding(false));
    }

    public string? JournalTodo(string text) { AppendLine(DayFile(DateTime.Today), $"- [ ] {text}"); return Ok($"To-do: {text}"); }

    public string? AppendJournal(string text) { AppendLine(DayFile(DateTime.Today), $"[{DateTime.Now:HH:mm}] {text}"); return Ok($"Journal: {text}"); }

    /// <summary>Ticks the newest matching open checkbox in the last 14 days: exact match first, then contains.</summary>
    public string? JournalDone(string text)
    {
        static string Norm(string s) => Regex.Replace(s.Trim(), @"\s+", " ").ToLowerInvariant();
        var want = Norm(text);
        foreach (var exact in new[] { true, false })
            for (int back = 0; back <= 14; back++)
            {
                var f = DayFile(DateTime.Today.AddDays(-back));
                if (!File.Exists(f)) continue;
                var lines = File.ReadAllLines(f);
                for (int i = 0; i < lines.Length; i++)
                {
                    var m = INotes.AnyCheckRx.Match(lines[i]);
                    if (!m.Success || m.Groups["mark"].Value != " ") continue;
                    var item = Norm(m.Groups["text"].Value);
                    if (exact ? item != want : !item.Contains(want)) continue;
                    lines[i] = m.Groups["pre"].Value + "x" + m.Groups["post"].Value + m.Groups["text"].Value;
                    File.WriteAllLines(f, lines);
                    Acked?.Invoke($"Ticked off: {m.Groups["text"].Value.Trim()}", true, null);
                    return Ok("");
                }
            }
        return Ok($"Ticked off: {text}", error: "no matching unticked to-do");
    }

    public string? PageAppend(string page, string text, Action<bool, string?, JsonElement>? onAck = null)
    {
        var name = string.Concat(page.Split(Path.GetInvalidFileNameChars())).Trim();
        if (name.Length == 0) return Ok($"Page: {page}", onAck, "bad page name");
        AppendLine(Path.Combine(PagesDir, name + ".md"), text);
        return Ok($"Added to \"{page}\"", onAck);
    }

    public (bool Ok, string? Error)? FindAck(string id) => done.Contains(id) ? (true, null) : (false, "unknown request");
}
