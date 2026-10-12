using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Threading;

namespace Vex.Services;

/// <summary>
/// Wires the senses together: runs the services on a timer, decides when to be quiet, builds the
/// context Vex's brain sees, and carries out the actions the brain asks for.
/// </summary>
public static class Hub
{
    public static VexWindow Vex { get; private set; } = null!;
    public static Presence Presence { get; } = new();
    public static Activity Activity { get; } = new();
    public static Notebook Notebook { get; } = new();
    public static Calendar Calendar { get; } = new();
    public static ClaudeWatch Claude { get; } = new();
    public static Nudges Nudges { get; } = new();

    /// <summary>In a call, or something's full screen: no wellbeing nudges.</summary>
    public static bool Quiet => InCall != null || FullScreen;
    public static string? InCall { get; private set; }
    public static bool FullScreen { get; private set; }

    static readonly string MemoryDir = Path.Combine(Store.Dir, "memory");
    static readonly string AboutPath = Path.Combine(MemoryDir, "about-me.md");
    static readonly string NotesPath = Path.Combine(MemoryDir, "notes.md");

    static DispatcherTimer? fast, slow;

    public static void Start(VexWindow vex)
    {
        Vex = vex;
        Store.SaveConfig();
        EnsureMemory();

        Notebook.Start();
        Notebook.Acked += (what, ok, err) => Vex.Say(ok ? $"Notebook: {what}" : $"The notebook said no: {err ?? "unknown error"}.", 5);
        Calendar.Refresh();
        Claude.Start();
        Claude.Changed += Nudges.OnClaude;
        Presence.BackFromBreak += WelcomeBack;
        if (Activity.Today.Count > 0) Presence.SeenSince(Activity.Today[0].Start);
        MigrateMemory();

        fast = new DispatcherTimer(TimeSpan.FromSeconds(5), DispatcherPriority.Background, (_, _) => Fast(), Vex.Dispatcher);
        slow = new DispatcherTimer(TimeSpan.FromSeconds(15), DispatcherPriority.Background, (_, _) => Slow(), Vex.Dispatcher);
        fast.Start(); slow.Start();
    }

    static void Fast()
    {
        Store.RollDay();
        Presence.Tick();
        Activity.Tick(Presence.Away);
        FullScreen = Activity.FullScreen();
    }

    static void Slow()
    {
        InCall = Activity.MicInUse();
        Calendar.Tick();
        Nudges.Tick();
        MigrateMemory();
    }

    public static void Stop() => Activity.Flush();

    static void WelcomeBack(TimeSpan away)
    {
        if (away < TimeSpan.FromMinutes(15) || Quiet) return;
        var next = Calendar.Upcoming.FirstOrDefault(m => m.Start > DateTime.Now && m.Start - DateTime.Now < TimeSpan.FromMinutes(45));
        var line = next != null ? $"Welcome back. {next.Subject} at {next.Start:HH:mm}." : "Oh. You're back. Nothing happened. I checked.";
        Vex.Say(line, 6);
    }

    // ───────────── what the brain sees ─────────────

    public static string Context()
    {
        var now = DateTime.Now;
        var sb = new StringBuilder();
        sb.AppendLine($"Now: {now:dddd d MMMM yyyy, HH:mm}");
        var p = Presence;
        sb.Append($"At desk since {p.FirstSeenToday:HH:mm}; ");
        sb.Append(p.OnBreak ? "currently on a break" : $"sitting for {(int)p.Sitting.TotalMinutes} min");
        if (p.Breaks.Count > 0) { var b = p.Breaks[^1]; sb.Append($"; last break {b.Start:HH:mm}–{b.End:HH:mm}"); }
        sb.AppendLine($"; coffees today: {Store.State.Coffees}");
        if (InCall != null) sb.AppendLine($"In a call (microphone in use by {InCall}).");

        if (Activity.CurrentApp is { Length: > 0 } app && app != Activity.Away)
            sb.AppendLine($"In front now: {app}{(Activity.CurrentTitle.Length > 0 ? $" — \"{Activity.CurrentTitle}\"" : "")}");
        var recent = Activity.RecentWork(8);
        if (recent.Count > 0)
        {
            sb.AppendLine("Recent windows (newest first):");
            foreach (var s in recent) sb.AppendLine($"- {s.Start:HH:mm} {s.App}: \"{s.Title}\" ({Math.Max(1, (int)(s.Secs / 60))} min)");
        }
        var top = Activity.TopApps(now.Date);
        if (top.Count > 0) sb.AppendLine("Time today by app: " + string.Join(", ", top.Select(t => $"{t.App} {Hm(t.Minutes)}")));

        if (Calendar.Today.Count > 0)
        {
            sb.AppendLine($"Meetings today (from {Calendar.Source}):");
            foreach (var m in Calendar.Today.Where(m => !m.AllDay))
                sb.AppendLine($"- {m.Start:HH:mm}–{m.End:HH:mm} {m.Subject}{(m.Location.Length > 0 ? $" ({m.Location})" : "")}{(m.End < now ? " [done]" : m.Start <= now ? " [now]" : "")}");
        }
        else sb.AppendLine(Calendar.Source == "none" ? "Calendar: not connected." : "No meetings today.");

        if (Notebook.Linked)
        {
            var checks = Notebook.OpenChecks.Take(20).ToList();
            sb.AppendLine(checks.Count == 0 ? "Open journal to-dos: none." : "Open journal to-dos (unticked checkboxes, last 14 days):");
            foreach (var c in checks) sb.AppendLine($"- {c.Text}{(c.Date == Store.Today ? "" : $" (from {c.Date})")}");
            var open = Notebook.Open.Take(10).ToList();
            if (open.Count > 0)
            {
                sb.AppendLine("Older items on the notebook's separate To-Do list:");
                foreach (var t in open) sb.AppendLine($"- {t.Text}{(t.Due != null ? $" (due {t.Due})" : "")}{(t.Project.Length > 0 ? $" [{t.Project}]" : "")}");
            }
            if (Notebook.TodayJournal.Length > 0) sb.AppendLine("Today's journal (latest):\n" + Tail(Notebook.TodayJournal, 700));
        }
        else sb.AppendLine("Lab Notebook: not linked.");

        var sessions = Claude.Sessions.Values.Where(s => now - s.Since < TimeSpan.FromHours(4)).ToList();
        if (sessions.Count > 0)
        {
            sb.AppendLine("Claude Code sessions:");
            foreach (var s in sessions)
                sb.AppendLine($"- {s.Project}: {s.State switch { ClaudeState.Working => "working", ClaudeState.NeedsYou => $"needs Ben ({s.Message})", _ => "finished, waiting for Ben" }} since {s.Since:HH:mm}");
        }
        if (Store.State.Reminders.Count > 0)
            sb.AppendLine("Pending reminders: " + string.Join("; ", Store.State.Reminders.OrderBy(r => r.At).Select(r => $"{r.At:HH:mm} {r.Text}")));

        var pages = Notebook.VexPages.ToList();
        if (pages.Count > 0)
        {
            // Vex's own notebook project. Ben edits these too, so they're the source of truth.
            sb.AppendLine($"Your pages in the Lab Notebook project \"{Notebook.VexProject}\" (Ben can edit them):");
            int budget = 5000;
            foreach (var pg in pages.OrderBy(pg => pg.Title.StartsWith("About", StringComparison.OrdinalIgnoreCase) ? 0 : 1))
            {
                var body = Tail(pg.Body, Math.Max(200, Math.Min(1500, budget)));
                sb.AppendLine($"## {pg.Title}\n{body}");
                budget -= body.Length;
                if (budget <= 0) { sb.AppendLine("(more pages not shown)"); break; }
            }
        }
        else
        {
            var about = Read(AboutPath);
            if (about.Trim().Length > 0) sb.AppendLine("About Ben (his notes):\n" + Tail(about, 1500));
            var notes = Read(NotesPath);
            if (notes.Trim().Length > 0) sb.AppendLine("Things you've been asked to remember:\n" + Tail(notes, 1500));
        }
        return sb.ToString();
    }

    static string Hm(double minutes) => minutes >= 60 ? $"{(int)(minutes / 60)}h{(int)(minutes % 60):00}" : $"{(int)minutes}m";
    static string Tail(string s, int n) => s.Length <= n ? s.Trim() : "…" + s[^n..].Trim();
    static string Read(string path) { try { return File.Exists(path) ? File.ReadAllText(path) : ""; } catch { return ""; } }

    // ───────────── what the brain can do ─────────────

    public const string ActionHelp = """
        You can take actions by adding lines at the very END of your reply, one per line, exactly like this:
        [[todo: text]]            adds a checkbox to today's journal (Ben's to-do list lives in the journal)
        [[done: text]]            ticks off an open checkbox (searches the last 14 days)
        [[journal: text]]         a timestamped note in today's journal ("note this down", progress, decisions)
        [[remember: fact]]        adds a fact about Ben to your "Remembered" page
        [[page: Page title | markdown]]  adds to a page in your own notebook project "Vex" (created if missing);
                                  use it to organise longer-lived information: people, projects, how-tos
        [[remind: HH:mm | text]]  or  [[remind: 20m | text]]
        Only act when Ben asks or clearly implies it. Never mention this syntax; Vex's app hides it and confirms.
        """;

    static readonly Regex ActionRx = new(@"\[\[\s*([a-z.]+)\s*:\s*(.*?)\s*\]\]", RegexOptions.Singleline);

    /// <summary>Strips action lines from a reply, runs them, and returns (clean reply, confirmations).</summary>
    public static (string Reply, string[] Done) RunActions(string reply)
    {
        var done = ActionRx.Matches(reply).Select(m => Do(m.Groups[1].Value, m.Groups[2].Value)).ToArray();
        return (ActionRx.Replace(reply, "").Trim(), done);
    }

    static string Do(string action, string arg)
    {
        var parts = arg.Split('|', 2, StringSplitOptions.TrimEntries);
        switch (action)
        {
            case "todo" or "todo.add":
            {
                var text = parts.Length > 1 && Regex.Match(parts[1], @"\d{4}-\d{2}-\d{2}") is { Success: true } d ? $"{parts[0]} (due {d.Value})" : parts[0];
                return Notebook.JournalTodo(text) != null ? $"✓ To-do: ☐ {text}{Queued()}" : NoNotebook();
            }
            case "done" or "todo.done":
                return Notebook.JournalDone(arg) != null ? $"✓ Ticking off: {arg}{Queued()}" : NoNotebook();
            case "journal" or "note":
                return Notebook.AppendJournal(arg) != null ? $"✓ Journal: [{DateTime.Now:HH:mm}] {arg}{Queued()}" : NoNotebook();
            case "page":
                if (parts.Length < 2) return "✗ Couldn't read that page note.";
                return Notebook.PageAppend(parts[0], parts[1]) != null ? $"✓ Added to page \"{parts[0]}\"{Queued()}" : NoNotebook();
            case "remind":
            {
                if (parts.Length < 2) return "✗ Couldn't read that reminder.";
                DateTime at;
                var m = Regex.Match(parts[0], @"^(\d+)\s*m");
                if (m.Success) at = DateTime.Now.AddMinutes(int.Parse(m.Groups[1].Value));
                else if (TimeSpan.TryParse(parts[0], CultureInfo.InvariantCulture, out var t)) at = DateTime.Today + t < DateTime.Now ? DateTime.Today.AddDays(1) + t : DateTime.Today + t;
                else return "✗ Couldn't read that reminder time.";
                Store.State.Reminders.Add(new Reminder { At = at, Text = parts[1] });
                Store.SaveState();
                return $"✓ Reminder at {at:HH:mm}: {parts[1]}";
            }
            case "remember":
                var fact = $"- {DateTime.Now:yyyy-MM-dd}: {arg}";
                if (Store.State.MemoryMigrated && Notebook.PageAppend("Remembered", fact) != null) return $"✓ Remembered: {arg}{Queued()}";
                File.AppendAllText(NotesPath, fact + Environment.NewLine);
                return $"✓ Remembered: {arg}";
            default:
                return $"✗ Unknown action: {action}";
        }
    }

    static string Queued() => Notebook.Running ? "" : " (queued until Lab Notebook is open)";
    static string NoNotebook() => "✗ Lab Notebook isn't linked to a data file, so I couldn't do that.";

    static void EnsureMemory()
    {
        Directory.CreateDirectory(MemoryDir);
        if (!File.Exists(AboutPath))
            File.WriteAllText(AboutPath, """
                # About me (Vex reads this; edit freely)

                - Role:
                - Main projects:
                - People I work with:
                - How I like to be nudged:
                """);
        if (!File.Exists(NotesPath)) File.WriteAllText(NotesPath, "");
    }

    public static string MemoryFolder => MemoryDir;

    static DateTime lastMigrateFail = DateTime.MinValue;
    static readonly string[] MemoryPages = { "About Ben", "Remembered" };

    /// <summary>
    /// One-off move of the local memory files into the notebook's "Vex" project ("About Ben" and
    /// "Remembered" pages). Each page is sent once and its request id kept in state; it's only resent
    /// (with a new id) after the notebook explicitly rejects it, never while it's still waiting, so the
    /// text can't land twice. When both are in, the local files are renamed to *.migrated.
    /// </summary>
    static void MigrateMemory()
    {
        var st = Store.State;
        if (st.MemoryMigrated || !Notebook.Linked) return;
        foreach (var page in MemoryPages)
        {
            if (st.MigratedPages.Contains(page)) continue;
            if (st.MigrationIds.TryGetValue(page, out var id))
            {
                var ack = Notebook.FindAck(id);
                if (ack == null) continue;                       // still queued: wait for it
                st.MigrationIds.Remove(page);
                if (ack.Value.Ok) { st.MigratedPages.Add(page); Store.SaveState(); continue; }
                Store.Log($"Memory migration ({page}): {ack.Value.Error}");
                lastMigrateFail = DateTime.Now;
                Store.SaveState();
            }
            if (DateTime.Now - lastMigrateFail < TimeSpan.FromMinutes(10)) continue;
            var sent = Notebook.PageAppend(page, MigrationText(page), (_, _, _) => { }); // quiet; checked above
            if (sent != null) { st.MigrationIds[page] = sent; Store.SaveState(); }
        }
        if (MemoryPages.All(st.MigratedPages.Contains))
        {
            st.MemoryMigrated = true; Store.SaveState();
            foreach (var f in new[] { AboutPath, NotesPath })
                try { if (File.Exists(f)) File.Move(f, f + ".migrated", overwrite: true); } catch { }
            Vex.Say("Moved my notes about you into the Lab Notebook, project \"Vex\". Edit away.", 8);
        }
    }

    static string MigrationText(string page)
    {
        if (page == "About Ben")
        {
            var about = string.Join("\n", Read(AboutPath).Split('\n').Where(l => !l.StartsWith("# About me"))).Trim();
            return about.Length > 0 ? about : "- Role:\n- Main projects:\n- People I work with:\n- How I like to be nudged:";
        }
        var notes = Read(NotesPath).Trim();
        return notes.Length > 0 ? notes : "Things Vex has been asked to remember.";
    }
}
