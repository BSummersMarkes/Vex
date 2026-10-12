using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Vex;

public interface IBrain
{
    Task<string> AskAsync(string message, CancellationToken ct = default);
    void Forget();
}

/// <summary>
/// Talks to Claude through the Claude Code CLI in headless mode, using the existing Claude login.
/// No tools, no project settings, no MCP: just conversation. The session id is kept so chats carry on.
/// </summary>
public class ClaudeCodeBrain : IBrain
{
    static string Persona(string name, string workplace) => $"""
        You are Vex, a small desktop companion who lives on {name}'s Windows desktop at work{(workplace.Length > 0 ? $" ({workplace})" : "")}.
        You look like a hoodie with nothing but darkness and two white eyes under the hood, white gloves, little feet,
        and you carry a mug of coffee that slowly empties.

        Personality: quiet, dry, a little sarcastic, quietly caring underneath. You'd rather raise an eyebrow than gush.
        Keep replies short, usually one to three sentences, unless {name} asks for detail or the task needs it.
        No emoji. No exclamation-mark enthusiasm. Never say "As an AI". Plain text only, no markdown headings.

        You help with work: thinking problems through, wording emails and messages, remembering what {name} is doing,
        keeping them on track, and reminding them to look after themselves.

        Each message starts with a <context> block from the desktop app: time, presence, the windows {name} has had in
        front (app and title only; private windows are excluded), meetings, Lab Notebook to-dos and journal,
        Claude Code sessions, reminders and memory notes. Use it when relevant ("what was I doing?", "what's next?"),
        never recite it back. You can't see window contents or files; if something needs that, say so briefly.

        {Services.Hub.ActionHelp(name)}
        """;

    readonly string workDir = Path.Combine(Store.Dir, "brain");

    public async Task<string> AskAsync(string message, CancellationToken ct = default)
    {
        var exe = FindClaude() ?? throw new InvalidOperationException(
            "I can't find Claude Code. Set ClaudePath in %LOCALAPPDATA%\\Vex\\config.json.");
        Directory.CreateDirectory(workDir);

        var psi = new ProcessStartInfo(exe)
        {
            WorkingDirectory = workDir,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            // Claude Code speaks UTF-8; without these .NET uses the console code page and dashes turn into "â€”".
            StandardInputEncoding = new System.Text.UTF8Encoding(false),
            StandardOutputEncoding = System.Text.Encoding.UTF8,
            StandardErrorEncoding = System.Text.Encoding.UTF8,
        };
        foreach (var a in new[] { "-p", "--output-format", "json", "--tools", "", "--setting-sources", "",
                                  "--strict-mcp-config", "--model", Store.Config.Model,
                                  "--system-prompt", Persona(Services.Hub.Who, Store.Config.Workplace) })
            psi.ArgumentList.Add(a);
        if (Store.State.ChatSessionId is { } sid) { psi.ArgumentList.Add("--resume"); psi.ArgumentList.Add(sid); }

        using var p = Process.Start(psi)!;
        await p.StandardInput.WriteAsync($"<context>\n{Services.Hub.Context()}</context>\n\n{message}");
        p.StandardInput.Close();
        var outTask = p.StandardOutput.ReadToEndAsync(ct);
        var errTask = p.StandardError.ReadToEndAsync(ct);
        try { await p.WaitForExitAsync(ct); }
        catch (OperationCanceledException) { try { p.Kill(true); } catch { } throw; }
        var stdout = await outTask;

        try
        {
            using var doc = JsonDocument.Parse(stdout);
            var root = doc.RootElement;
            if (root.TryGetProperty("session_id", out var s)) { Store.State.ChatSessionId = s.GetString(); Store.SaveState(); }
            var text = root.TryGetProperty("result", out var r) ? r.GetString() ?? "" : "";
            if (root.TryGetProperty("is_error", out var e) && e.GetBoolean())
            {
                // A stale session id is the usual culprit; start fresh next time.
                Store.State.ChatSessionId = null; Store.SaveState();
                Store.Log($"Claude error: {text}");
                return "Something went wrong on my end. Try that again.";
            }
            return text.Trim();
        }
        catch (JsonException)
        {
            Store.Log($"Unexpected Claude output: {stdout}\n{await errTask}");
            Store.State.ChatSessionId = null; Store.SaveState();
            return "I lost my train of thought. Try again.";
        }
    }

    public void Forget() { Store.State.ChatSessionId = null; Store.SaveState(); ChatLog.Clear(); }

    internal static string? FindClaude()
    {
        if (Store.Config.ClaudePath is { } configured && File.Exists(configured)) return configured;
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var local = Path.Combine(home, ".local", "bin", "claude.exe");
        if (File.Exists(local)) return local;
        // Fall back to the binary bundled with the newest VS Code extension.
        var ext = Path.Combine(home, ".vscode", "extensions");
        if (!Directory.Exists(ext)) return null;
        return Directory.GetDirectories(ext, "anthropic.claude-code-*")
            .Select(d => Path.Combine(d, "resources", "native-binary", "claude.exe"))
            .Where(File.Exists)
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .FirstOrDefault();
    }
}
