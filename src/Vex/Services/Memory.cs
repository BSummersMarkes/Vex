using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace Vex.Services;

/// <summary>
/// Vex's own memory: a folder of markdown pages he owns ("About Ben", "Remembered", "Diary", "Projects", ...).
/// It's the source of truth. The Lab Notebook mirrors it (reads the files and writes edits back in place),
/// so Vex never depends on the notebook running to remember something. Contract: SPEC.md section 8.
///   %LOCALAPPDATA%\Vex\memory\{Page title}.md     (or MemoryFolder in config.json)
/// Files are read fresh each time, so edits made in an editor or the notebook are picked up straight away.
/// </summary>
public static class Memory
{
    public const string Remembered = "Remembered";
    public const string Diary = "Diary";

    /// <summary>MemoryFolder from config, else %LOCALAPPDATA%\Vex\memory.</summary>
    public static string Folder => Store.Config.MemoryFolder is { Length: > 0 } f ? f : Path.Combine(Store.Dir, "memory");

    public static string AboutPage => $"About {(Store.Config.UserName.Length > 0 ? Store.Config.UserName : "me")}";

    public record Page(string Title, string Body);

    /// <summary>Creates the folder and the starter pages, and brings older layouts across (once).</summary>
    public static void Start()
    {
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
