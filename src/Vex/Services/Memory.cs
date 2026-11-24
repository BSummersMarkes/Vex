using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace Vex.Services;

/// <summary>
/// Vex's own memory: a folder of markdown pages he owns ("About Ben", "Remembered", "Diary", "Projects", ...).
/// It's the source of truth, and the one copy: the Lab Notebook shows the same files (reads them and writes edits back
/// in place), so Vex never depends on the notebook running to remember something. Contract: SPEC.md section 8.
///   {MemoryFolder}\{Page title}.md     (default: "Vex memory" beside the notebook's data file, else in Documents)
/// Files are read fresh each time, so edits made in an editor or the notebook are picked up straight away.
/// </summary>
public static class Memory
{
    public const string Remembered = "Remembered";
    public const string Diary = "Diary";

    /// <summary>MemoryFolder from config (set on the first start of this version), else the old place in AppData.</summary>
    public static string Folder => Store.Config.MemoryFolder is { Length: > 0 } f ? f : LegacyFolder;

    /// <summary>Where the pages lived before they moved out of AppData.</summary>
    static string LegacyFolder => Path.Combine(Store.Dir, "memory");

    /// <summary>
    /// Where the pages go when none has been chosen: a "Vex memory" folder beside the Lab Notebook's data file when the
    /// notebook is on this PC (so they sit with the notebook's data, synced the same way, one copy for both), else
    /// Documents\Vex memory. Outside AppData either way, so they're easy to find, back up and edit.
    /// </summary>
    public static string DefaultFolder =>
        Notebook.Detect() is { } data && Path.GetDirectoryName(data) is { Length: > 0 } dir
            ? Path.Combine(dir, "Vex memory")
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Vex memory");

    public static string AboutPage => $"About {(Store.Config.UserName.Length > 0 ? Store.Config.UserName : "me")}";

    public record Page(string Title, string Body);

    /// <summary>Creates the folder and the starter pages, and brings older layouts across (once).</summary>
    public static void Start()
    {
        // First start since the pages moved out of AppData: settle on the default folder, bring the pages over and
        // write the choice into config.json, so the notebook (and Setup) read one definite place.
        if (Store.Config.MemoryFolder is not { Length: > 0 })
        {
            var to = DefaultFolder;
            if (MoveTo(to) is { } err) Store.Log($"Memory: couldn't move the pages to {to} ({err}); staying in {Folder} for now.");
        }
        try
        {
            Directory.CreateDirectory(Folder);
            Adopt();
            if (!File.Exists(PathOf(AboutPage)))
                Write(AboutPage, "- Role:\n- Main projects:\n- People I work with:\n- How I like to be nudged:\n");
            if (!File.Exists(PathOf(Remembered))) Write(Remembered, "");
        }
        catch (Exception ex) { Store.Log($"Memory folder ({Folder}): {ex.Message}"); }
    }

    /// <summary>
    /// Earlier versions kept memory as pages in the local notes folder's Vex subfolder (newest, if used), or as
    /// memory\about-me.md and notes.md (renamed *.migrated once sent to the notebook). Gathers them into pages here,
    /// once: the notes-folder pages first, then any old-file text not already in them is appended, so nothing is lost.
    /// </summary>
    static void Adopt()
    {
        if (Store.State.MemoryAdopted) return;
        if (Store.Config.NotesFolder is { } nf && Path.Combine(nf, "Vex") is var pagesDir && Directory.Exists(pagesDir)
            && !pagesDir.Equals(Folder, StringComparison.OrdinalIgnoreCase))
            foreach (var f in Directory.GetFiles(pagesDir, "*.md"))
            {
                var dest = Path.Combine(Folder, Path.GetFileName(f));
                if (!File.Exists(dest)) File.Copy(f, dest);
            }
        var old = Path.Combine(Store.Dir, "memory");
        foreach (var (file, page) in new[] { ("about-me.md", AboutPage), ("notes.md", Remembered) })
            foreach (var path in new[] { Path.Combine(old, file), Path.Combine(old, file + ".migrated") })
            {
                if (!File.Exists(path)) continue;
                var body = string.Join("\n", File.ReadAllLines(path).Where(l => !l.StartsWith("# About me"))).Trim();
                // The untouched starter template ("- Role:" etc. with nothing filled in) isn't worth keeping twice.
                bool template = body.Split('\n').All(l => l.Trim().Length == 0 || l.TrimEnd().EndsWith(':'));
                var existing = File.Exists(PathOf(page)) ? Read(page) : null;
                if (existing == null) Write(page, body.Length > 0 ? body + "\n" : "");
                else if (body.Length > 0 && !template && !existing.Contains(body)) Append(page, body);
                File.Move(path, path + ".old", overwrite: true);
            }
        Store.State.MemoryAdopted = true;
        Store.SaveState();
    }

    /// <summary>
    /// Makes <paramref name="to"/> the memory folder: every page in the current folder is moved there, then the choice is
    /// saved in config.json. A page that already exists there (same title) is kept as it is, and the old copy stays
    /// behind renamed "*.md.old", so nothing is ever overwritten or lost. Returns null, or why it couldn't.
    /// Used on first start (the move out of AppData) and by Setup → Privacy → Memory folder.
    /// </summary>
    public static string? MoveTo(string to)
    {
        to = to.Trim();
        if (to.Length == 0) return "no folder given";
        try
        {
            to = Path.GetFullPath(to);
            Directory.CreateDirectory(to);
            var from = Folder;
            if (!Path.GetFullPath(from).TrimEnd('\\').Equals(to.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase) && Directory.Exists(from))
            {
                int moved = 0;
                foreach (var f in Directory.GetFiles(from, "*.md"))
                {
                    // The pre-pages files (about-me.md, notes.md) are Adopt's to gather; leave them where it looks.
                    if (from == LegacyFolder && Path.GetFileName(f) is "about-me.md" or "notes.md") continue;
                    var dest = Path.Combine(to, Path.GetFileName(f));
                    try
                    {
                        if (File.Exists(dest)) File.Move(f, f + ".old", overwrite: true);
                        else { File.Move(f, dest); moved++; }
                    }
                    catch (IOException ex)
                    {
                        // Open in an editor, say: copy it instead so the page still comes along; the original stays put.
                        Store.Log($"Memory: couldn't move {f} ({ex.Message}), copying it instead");
                        if (!File.Exists(dest)) { File.Copy(f, dest); moved++; }
                    }
                }
                // A pointer for anyone (or anything) still looking in the old place.
                try { File.WriteAllText(Path.Combine(from, "MOVED.txt"), $"Vex's memory pages are now in:\r\n{to}\r\n"); } catch { }
                Store.Log($"Memory: moved {moved} page(s) from {from} to {to}");
            }
            Store.Config.MemoryFolder = to;
            Store.SaveConfig();
            return null;
        }
        catch (Exception ex) { return ex.Message; }
    }

    static string Safe(string title) => string.Concat(title.Split(Path.GetInvalidFileNameChars())).Trim();
    static string PathOf(string title) => Path.Combine(Folder, Safe(title) + ".md");

    /// <summary>All pages, About first, then the rest by title.</summary>
    public static List<Page> Pages()
    {
        try
        {
            if (!Directory.Exists(Folder)) return new();
            return Directory.GetFiles(Folder, "*.md")
                .Select(f => new Page(Path.GetFileNameWithoutExtension(f), ReadShared(f)))
                .OrderBy(p => p.Title.StartsWith("About", StringComparison.OrdinalIgnoreCase) ? 0 : 1).ThenBy(p => p.Title)
                .ToList();
        }
        catch (Exception ex) { Store.Log($"Memory read: {ex.Message}"); return new(); }
    }

    public static string Read(string title)
    {
        try { return File.Exists(PathOf(title)) ? ReadShared(PathOf(title)) : ""; } catch { return ""; }
    }

    /// <summary>Adds markdown to the end of a page, creating it if needed. Returns false if the name or disk won't have it.</summary>
    public static bool Append(string title, string text)
    {
        if (Safe(title).Length == 0) return false;
        try
        {
            Directory.CreateDirectory(Folder);
            var path = PathOf(title);
            var sep = File.Exists(path) && new FileInfo(path).Length > 0 && !ReadShared(path).EndsWith('\n') ? "\n" : "";
            File.AppendAllText(path, sep + text.TrimEnd() + "\n", new UTF8Encoding(false));
            return true;
        }
        catch (Exception ex) { Store.Log($"Memory write ({title}): {ex.Message}"); return false; }
    }

    static void Write(string title, string body) => File.WriteAllText(PathOf(title), body, new UTF8Encoding(false));

    /// <summary>Reads without getting in the way of the notebook (or an editor) saving the same file.</summary>
    static string ReadShared(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var sr = new StreamReader(fs, Encoding.UTF8);
        return sr.ReadToEnd();
    }

    /// <summary>The diary entries for the last few days (newest last), for the chat context.</summary>
    public static string RecentDiary(int days)
    {
        var body = Read(Diary);
        var entries = ("\n" + body).Split("\n## ").Skip(1).Select(e => "## " + e.Trim()).TakeLast(days);
        return string.Join("\n", entries);
    }

    public static bool HasDiaryFor(string date) => Read(Diary).Contains($"## {date}");
}
