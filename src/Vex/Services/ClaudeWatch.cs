using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using System.Windows.Threading;

namespace Vex.Services;

public enum ClaudeState { Working, NeedsYou, Finished }

public class ClaudeSession
{
    public string Id { get; init; } = "";
    public string Project { get; set; } = "";
    public ClaudeState State { get; set; }
    public string? Message { get; set; }
    public DateTime Since { get; set; } = DateTime.Now;
    public DateTime LastNudge { get; set; } = DateTime.MinValue;
    /// <summary>How long the last turn ran before finishing.</summary>
    public TimeSpan WorkedFor { get; set; }
}

/// <summary>
/// Hears from Claude Code sessions through hooks. Each hook pipes its JSON to a tiny localhost-only
/// HTTP listener here (curl does the posting), so Vex knows when a session finishes or needs you.
/// </summary>
public class ClaudeWatch
{
    public Dictionary<string, ClaudeSession> Sessions { get; } = new();
    /// <summary>Raised on the UI thread when a session changes state.</summary>
    public event Action<ClaudeSession>? Changed;

    readonly Dispatcher ui = Dispatcher.CurrentDispatcher;
    HttpListener? listener;

    static string Url => $"http://localhost:{Store.Config.HookPort}/";
    static string HookCommand => $"curl -s -m 2 --data-binary @- {Url}claude-hook";
    static readonly string SettingsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", "settings.json");
    static readonly string[] Events = { "UserPromptSubmit", "Notification", "Stop", "SessionEnd" };

    public void Start()
    {
        try
        {
            listener = new HttpListener();
            listener.Prefixes.Add(Url);
            listener.Start();
            _ = Task.Run(Loop);
        }
        catch (Exception ex) { Store.Log($"Hook listener couldn't start on {Url}: {ex.Message}"); listener = null; }
    }

    public bool Listening => listener?.IsListening == true;

    async Task Loop()
    {
        while (listener?.IsListening == true)
        {
            HttpListenerContext ctx;
            try { ctx = await listener.GetContextAsync(); } catch { return; }
            try
            {
                string body;
                using (var sr = new StreamReader(ctx.Request.InputStream, Encoding.UTF8)) body = await sr.ReadToEndAsync();
                var path = ctx.Request.Url?.AbsolutePath ?? "";
                string reply = "";
                if (path == "/claude-hook") _ = ui.BeginInvoke(() => Handle(body));
                else if (Store.Config.DebugEndpoints && path.StartsWith("/debug/"))
                    reply = await ui.InvokeAsync(() => Debug(path, body));
                var bytes = Encoding.UTF8.GetBytes(reply);
                ctx.Response.StatusCode = 200;
                ctx.Response.ContentType = "text/plain; charset=utf-8";
                await ctx.Response.OutputStream.WriteAsync(bytes);
                ctx.Response.Close();
            }
            catch (Exception ex) { Store.Log($"Hook request: {ex.Message}"); }
        }
    }

    /// <summary>Testing aids, only when DebugEndpoints is on in config.json (localhost only).</summary>
    static string Debug(string path, string body) => path switch
    {
        "/debug/context" => Hub.Context(),
        "/debug/actions" => string.Join("\n", Hub.RunActions(body).Done),
        "/debug/act" => Hub.Vex.DebugAct(body),
        "/debug/drop" => Hub.Vex.DebugDrop(body),
        "/debug/perch" => Hub.Vex.DebugPerch(),
        "/debug/ledges" => Hub.Vex.DebugLedges(body),
        "/debug/say" => Do(() => Hub.Nudges.Offer(TestNudge(body))),
        _ => "unknown",
    };

    static string Do(Func<bool> f) => f() ? "shown" : "held back";

    static Nudge TestNudge(string text)
    {
        var n = new Nudge { Type = "test", Exempt = true, Text = text, Seconds = 15 };
        n.Buttons.Add(("Done", () => { }));
        n.Buttons.Add(("✕", () => { }));
        return n;
    }

    void Handle(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var r = doc.RootElement;
            string? Get(string n) => r.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
            var id = Get("session_id") ?? "?";
            var ev = Get("hook_event_name") ?? "";
            if (ev == "SessionEnd") { Sessions.Remove(id); return; }

            bool isNew = !Sessions.TryGetValue(id, out var s);
            if (s == null) Sessions[id] = s = new ClaudeSession { Id = id };
            var cwd = Get("cwd");
            if (!string.IsNullOrEmpty(cwd)) s.Project = Path.GetFileName(cwd.TrimEnd('\\', '/'));
            if (isNew) Store.Log($"Claude Code session reporting in: {s.Project} ({ev})");

            var state = ev switch
            {
                "UserPromptSubmit" => ClaudeState.Working,
                "Notification" => ClaudeState.NeedsYou,
                "Stop" => ClaudeState.Finished,
                _ => s.State,
            };
            s.Message = ev == "Notification" ? Get("message") : null;
            if (s.State == ClaudeState.Working && state == ClaudeState.Finished) s.WorkedFor = DateTime.Now - s.Since;
            if (state != s.State || ev == "Notification") { s.State = state; s.Since = DateTime.Now; }
            Changed?.Invoke(s);
        }
        catch (Exception ex) { Store.Log($"Hook payload: {ex.Message}"); }
    }

    // ───────────── hooking into Claude Code ─────────────

    public static bool Installed()
    {
        try { return File.Exists(SettingsPath) && File.ReadAllText(SettingsPath).Contains($"localhost:{Store.Config.HookPort}/claude-hook"); }
        catch { return false; }
    }

    /// <summary>
    /// Adds Vex's hooks to ~/.claude/settings.json (backing it up first), leaving every other setting
    /// and hook as it was. Safe to run twice.
    /// </summary>
    public static string Install()
    {
        JsonObject root;
        if (File.Exists(SettingsPath))
        {
            File.Copy(SettingsPath, SettingsPath + ".vex-backup", overwrite: true);
            root = JsonNode.Parse(File.ReadAllText(SettingsPath)) as JsonObject ?? new JsonObject();
        }
        else { Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!); root = new JsonObject(); }

        var hooks = root["hooks"] as JsonObject ?? new JsonObject();
        root["hooks"] = hooks;
        int added = 0;
        foreach (var ev in Events)
        {
            var list = hooks[ev] as JsonArray ?? new JsonArray();
            hooks[ev] = list;
            if (list.ToJsonString().Contains("/claude-hook")) continue;
            list.Add(new JsonObject
            {
                ["hooks"] = new JsonArray(new JsonObject { ["type"] = "command", ["command"] = HookCommand, ["timeout"] = 5 }),
            });
            added++;
        }
        File.WriteAllText(SettingsPath, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        return added == 0 ? "Already connected." : $"Connected: {added} hooks added to {SettingsPath} (backup alongside).";
    }

    /// <summary>Brings the VS Code window for a project to the front, if there is one.</summary>
    public static bool FocusVsCode(string project)
    {
        var pids = Process.GetProcessesByName("Code").Select(p => (uint)p.Id).ToHashSet();
        var wins = Native.WindowsOf(pids);
        var h = wins.FirstOrDefault(w => Native.WindowText(w).Contains(project, StringComparison.OrdinalIgnoreCase));
        if (h == IntPtr.Zero) h = wins.FirstOrDefault();
        if (h == IntPtr.Zero) return false;
        if (Native.IsIconic(h)) Native.ShowWindow(h, Native.SW_RESTORE);
        return Native.SetForegroundWindow(h);
    }
}
