using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Vex.Services;

/// <summary>
/// Vex working on himself. Asked in chat ("add a…"), the brain hands the request over with a [[build: …]]
/// action and this runs it in the background, one job at a time:
///   1. a coder Claude Code session edits the source in <see cref="VexConfig.SourcePath"/>;
///   2. Vex publishes it himself into a staging folder (a failed build goes back to the coder);
///   3. a second, read-only Claude session reviews the staged diff (objections go back to the coder);
///   4. approved: commit to the current branch and push. Not approved after a few rounds: parked on a vex/… branch;
///   5. he knocks, and on "Restart now" a helper script swaps the new exe in, rolling back if it won't start.
/// Off unless SourcePath points at a git checkout, so shared copies of Vex never see any of this.
/// </summary>
public static class Workshop
{
    static VexConfig C => Store.Config;
    static string Repo => C.SourcePath ?? "";
    static readonly string Dir = Path.Combine(Store.Dir, "workshop");
    static readonly string OutDir = Path.Combine(Dir, "out");
    static string StagedExe => Path.Combine(OutDir, "Vex.exe");
    static readonly string SwapResult = Path.Combine(Dir, "swap-result.txt");
    const int Rounds = 3;   // coder ↔ build/review round trips before giving up

    public static bool Enabled => C.SourcePath is { Length: > 0 } && Directory.Exists(Path.Combine(Repo, ".git"));

    /// <summary>The request being worked on, or null when idle.</summary>
    public static string? Job { get; private set; }
    static string stage = "";
    static DateTime started;
    /// <summary>One line on how the last job went, for the brain's context.</summary>
    static string? lastOutcome;
    /// <summary>Whether the last job succeeded (null before the first), so Vex can nod or shrug as he shuts the laptop.</summary>
    public static bool? LastOk { get; private set; }
    /// <summary>A finished build waiting for a restart.</summary>
    static bool staged;
    /// <summary>A finish/fail notice not shown yet (e.g. something was full screen); retried from <see cref="Tick"/>.</summary>
    static Nudge? notice;
    static string log = "";

    // ───────────── from the brain ─────────────

    public static string Start(string request)
    {
        if (!Enabled) return "✗ My workshop isn't set up (SourcePath in config.json).";
        if (Job != null) return $"✗ Already working on \"{Job}\". One thing at a time.";
        if (string.IsNullOrWhiteSpace(request)) return "✗ Build what, exactly?";
        Job = request.Trim();
        started = DateTime.Now;
        _ = Run(Job);
        return $"✓ Workshop: on it. I'll knock when it's done.";
    }

    public static string Restart()
    {
        if (!staged || !File.Exists(StagedExe)) return "✗ There's no new version waiting.";
        if (Job != null) return $"✗ Not while I'm working on \"{Job}\".";
        SwapAndQuit();
        return "✓ Restarting.";
    }

    /// <summary>For the brain: what the workshop is up to.</summary>
    public static string? Status()
    {
        if (!Enabled) return null;
        if (Job != null) return $"Workshop (changing your own code): working on \"{Job}\" since {started:HH:mm}, currently {stage}.";
        var s = lastOutcome != null ? $"Workshop: last job {lastOutcome}" : "Workshop: idle.";
        return staged ? s + " A new version of you is built and waiting for a restart." : s;
    }

    /// <summary>Every 15 s from the hub: delivers a notice that couldn't be shown when the job ended.</summary>
    public static void Tick()
    {
        if (notice != null && Hub.Nudges.Offer(notice)) notice = null;
    }

    /// <summary>On startup: says whether the last swap took, as reported by the swap script.</summary>
    public static void Started()
    {
        try
        {
            if (!File.Exists(SwapResult)) return;
            var r = File.ReadAllText(SwapResult).Trim();
            File.Delete(SwapResult);
            Hub.Vex.Say(r == "ok" ? "New version. Feels the same. Probably a good sign."
                                  : "The new version fell over on startup, so you've got the old me back. Details are in the workshop log.", 8);
        }
        catch (Exception ex) { Store.Log($"Workshop swap result: {ex.Message}"); }
    }

    // ───────────── the job ─────────────

    static async Task Run(string request)
    {
        Directory.CreateDirectory(Dir);
        var logPath = Path.Combine(Dir, $"{DateTime.Now:yyyy-MM-dd_HHmmss}.log");
        log = logPath;
        Note($"Request: {request}");
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(C.WorkshopTimeoutMinutes));
        var ct = cts.Token;
        string? branch = null;
        try
        {
            // Never build on top of uncommitted work: it'd end up in Vex's commit.
            stage = "checking the repo";
            if ((await Git(ct, "status", "--porcelain")).Out.Trim().Length > 0)
            { Finish(false, "There are uncommitted changes in my source folder. Commit or stash them and ask again."); return; }
            branch = (await Git(ct, "rev-parse", "--abbrev-ref", "HEAD")).Out.Trim();
            if (branch is "" or "HEAD") { Finish(false, "My source folder isn't on a branch."); return; }
            var pull = await Git(ct, "pull", "--ff-only");
            if (pull.Code != 0) Note("Pull failed, carrying on with the local copy.");

            stage = "writing code";
            var coder = await Claude(CoderArgs(), $"Request from {Hub.Who}: {request}", C.WorkshopModel, null, ct);
            for (int round = 1; ; round++)
            {
                if (coder.Error != null) { await Park(request, ct); Finish(false, $"The coder gave up: {coder.Error}"); return; }

                stage = "building";
                var build = await Publish(ct);
                string? problem = null;
                if (!build.Ok) problem = "The build failed:\n" + Tail(build.Out, 4000);
                else
                {
                    await Git(ct, "add", "-A");
                    var diff = (await Git(ct, "diff", "--cached")).Out;
                    if (diff.Trim().Length == 0)
                    { Finish(false, $"Nothing changed. {Field(coder.Json, "summary") ?? ""}".Trim()); return; }

                    stage = "being reviewed";
                    var review = await Claude(ReviewerArgs(), ReviewPrompt(request, diff), C.WorkshopReviewModel, null, ct);
                    if (review.Json is { } rj && rj.TryGetProperty("approve", out var ok) && ok.GetBoolean())
                    {
                        await Ship(request, coder.Json, Field(rj, "notes"), branch, ct);
                        return;
                    }
                    await Git(ct, "reset", "-q");
                    problem = "The reviewer didn't approve. Issues:\n" + (review.Json is { } j && j.TryGetProperty("issues", out var iss)
                        ? string.Join("\n", iss.EnumerateArray().Select(i => "- " + i.GetString()))
                        : review.Error ?? "(no reason given)");
                }
                Note(problem);
                if (round >= Rounds)
                {
                    var parked = await Park(request, ct);
                    Finish(false, $"Couldn't get it past {(build.Ok ? "review" : "the build")} after {Rounds} goes. {(parked != null ? $"The attempt is on branch {parked}." : "")}");
                    return;
                }
                stage = "fixing";
                coder = await Claude(CoderArgs(), problem + "\n\nFix this, then finish with the same structured output.", C.WorkshopModel, coder.Session, ct);
            }
        }
        catch (OperationCanceledException)
        {
            if (branch != null) await Park(request, CancellationToken.None);
            Finish(false, $"Ran out of time ({C.WorkshopTimeoutMinutes:0} min).");
        }
        catch (Exception ex)
        {
            Store.Log($"Workshop: {ex}");
            if (branch != null) await Park(request, CancellationToken.None);
            Finish(false, $"Something broke: {ex.Message}");
        }
    }

    /// <summary>Approved: commit to the current branch, push, and offer the restart.</summary>
    static async Task Ship(string request, JsonElement? coder, string? reviewNotes, string branch, CancellationToken ct)
    {
        stage = "committing";
        var msg = (Field(coder, "commitMessage") ?? $"Vex workshop: {request}").Trim()
                  + $"\n\nRequested in chat with Vex; written and reviewed by his workshop (Claude Code).\n\nCo-Authored-By: Claude <noreply@anthropic.com>";
        var commit = await Git(ct, new[] { "commit", "-q", "-F", "-" }, msg);
        if (commit.Code != 0) { Finish(false, "Commit failed: " + Tail(commit.Out, 300)); return; }
        if (!string.IsNullOrWhiteSpace(reviewNotes)) Note("Reviewer notes: " + reviewNotes);

        string pushed = "";
        if (C.WorkshopPush)
        {
            stage = "pushing";
            var push = await Git(ct, "push", "origin", branch);
            pushed = push.Code == 0 ? $" Pushed to {branch}." : " Committed, but the push failed (it's in the log).";
            if (push.Code != 0) Note("Push: " + push.Out);
        }
        staged = true;
        var summary = Field(coder, "summary") ?? request;
        Finish(true, $"Done: {summary}{pushed}");
    }

    /// <summary>Moves unfinished work onto a vex/… branch so the working branch stays clean. Returns its name.</summary>
    static async Task<string?> Park(string request, CancellationToken ct)
    {
        try
        {
            await Git(ct, "add", "-A");
            if ((await Git(ct, "diff", "--cached", "--quiet")).Code == 0) return null;   // nothing to keep
            var current = (await Git(ct, "rev-parse", "--abbrev-ref", "HEAD")).Out.Trim();
            var name = "vex/" + Slug(request) + DateTime.Now.ToString("-MMdd-HHmm");
            await Git(ct, "switch", "-q", "-c", name);
            await Git(ct, new[] { "commit", "-q", "-F", "-" }, $"WIP (Vex workshop, not approved): {request}");
            await Git(ct, "switch", "-q", current);
            return name;
        }
        catch (Exception ex) { Note($"Couldn't park the attempt: {ex.Message}"); return null; }
    }

    static void Finish(bool ok, string text)
    {
        Note(text);
        lastOutcome = $"\"{Job}\" {(ok ? "succeeded" : "failed")} at {DateTime.Now:HH:mm}: {text}";
        LastOk = ok;   // before Job clears: that's what tells Vex the job is over
        Job = null;
        stage = "";
        ChatLog.Append("vex", text);
        var n = Notice(ok, text, log);
        if (!Hub.Nudges.Offer(n)) notice = n;
    }

    /// <summary>The knock with a job's result; also remade from the chat's Pending tab after a restart.</summary>
    internal static Nudge Notice(bool ok, string text, string logPath)
    {
        var n = new Nudge { Type = "workshop", Exempt = true, Seconds = 60, Text = text, PendingKey = "workshop", Args = { ["log"] = logPath } };
        if (ok) { n.Buttons.Add(("Restart now", () => Restart())); n.Buttons.Add(("Later", () => { })); }
        else { n.Buttons.Add(("Show log", () => Open(logPath))); n.Buttons.Add(("✕", () => { })); }
        return n;
    }

    // ───────────── the restart ─────────────

    /// <summary>
    /// Hands over to a small PowerShell script (a running exe can't overwrite itself): it waits for this process
    /// to exit, keeps the current exe as Vex.exe.old, copies the staged one in and starts it. If the new one
    /// isn't still running after 15 s it puts the old one back. Either way it leaves a note for <see cref="Started"/>.
    /// </summary>
    static void SwapAndQuit()
    {
        var target = Environment.ProcessPath!;
        var script = Path.Combine(Dir, "swap.ps1");
        File.WriteAllText(script, """
            param([int]$OldPid, [string]$New, [string]$Target, [string]$Result)
            Wait-Process -Id $OldPid -Timeout 30 -ErrorAction SilentlyContinue
            # Vex stops his watchdog (a second copy of the exe) as he quits; make sure it's gone before overwriting the exe.
            Get-Process -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $Target } | Wait-Process -Timeout 10 -ErrorAction SilentlyContinue
            Start-Sleep -Milliseconds 500
            try {
                Copy-Item $Target "$Target.old" -Force
                Copy-Item $New $Target -Force
                $p = Start-Process $Target -PassThru
                Start-Sleep -Seconds 15
                if ($p.HasExited) { throw "new version exited with code $($p.ExitCode)" }
                Set-Content $Result 'ok'
            } catch {
                Set-Content $Result "rolled back: $_"
                # The new version's watchdog may have restarted it meanwhile; stop both so the exe can be put back.
                Get-Process -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $Target } | Stop-Process -Force -ErrorAction SilentlyContinue
                Start-Sleep -Milliseconds 300
                Copy-Item "$Target.old" $Target -Force
                Start-Process $Target
            }
            """);
        var psi = new ProcessStartInfo("powershell.exe") { UseShellExecute = false, CreateNoWindow = true };
        foreach (var a in new[] { "-NoProfile", "-ExecutionPolicy", "Bypass", "-WindowStyle", "Hidden", "-File", script,
                                  "-OldPid", Environment.ProcessId.ToString(), "-New", StagedExe, "-Target", target, "-Result", SwapResult })
            psi.ArgumentList.Add(a);
        Process.Start(psi);
        ((App)System.Windows.Application.Current).Quit();
    }

    // ───────────── Claude sessions ─────────────

    /// <summary>
    /// The coder: file tools confined to the repo (--restricted), edits auto-accepted, and Bash only for
    /// dotnet build and read-only git. Anything else is refused automatically; nobody's there to ask.
    /// </summary>
    static string[] CoderArgs() => new[]
    {
        "--restricted", "--tools", "Read,Edit,Write,Glob,Grep,Bash", "--permission-mode", "acceptEdits",
        "--permission-prompts", "none",
        "--allowedTools", "Bash(dotnet build:*)", "Bash(dotnet restore:*)", "Bash(git status:*)", "Bash(git diff:*)", "Bash(git log:*)",
        "--append-system-prompt", CoderRules(),
        "--json-schema", """{"type":"object","properties":{"summary":{"type":"string"},"commitMessage":{"type":"string"}},"required":["summary","commitMessage"]}""",
    };

    static string CoderRules() => $"""
        You are the workshop for Vex, a C#/.NET 10 WPF desktop companion, changing Vex's own source code at the request of
        {Hub.Who}, passed on from a chat with Vex. Nobody is watching this session, so don't ask questions; decide and note it.

        - Read SPEC.md and the relevant code first. Match the surrounding style: naming, idioms, layout and comment density.
        - Comment the change properly: an XML doc <summary> on new types and members, and short comments on anything
          non-obvious. {Hub.Who} reads these later to understand what changed and why, so explain intent, not mechanics.
        - Keep it focused on the request: no unrelated refactors or reformatting.
        - Update SPEC.md when behaviour changes (the phases list and any affected section), and README.md if it's user-facing.
        - SPEC.md section 2's principles are hard rules: nothing new leaves the machine, exclusions are respected,
          Vex stays out of the way. Tweakable numbers go in VexConfig (config.json), not hard-coded.
        - Check it compiles: dotnet build src/Vex/Vex.csproj -c Release. Never run build.ps1 (it kills the running Vex),
          never run Vex.exe, and don't commit; Vex builds, gets it reviewed and commits for you.
        - If the request is unclear, unsafe or would break a principle, change nothing and say why in the summary.

        Finish with: summary (two or three plain sentences for {Hub.Who}: what changed and how to use it) and
        commitMessage (a subject line under 72 characters, a blank line, then a short body).
        """;

    /// <summary>The reviewer: a fresh session that can only read.</summary>
    static string[] ReviewerArgs() => new[]
    {
        "--restricted", "--tools", "Read,Glob,Grep", "--permission-mode", "dontAsk", "--permission-prompts", "none",
        "--json-schema", """{"type":"object","properties":{"approve":{"type":"boolean"},"issues":{"type":"array","items":{"type":"string"}},"notes":{"type":"string"}},"required":["approve","issues"]}""",
    };

    static string ReviewPrompt(string request, string diff) => $"""
        You're reviewing a change to Vex, a C#/.NET 10 WPF desktop companion, before it's committed to {Hub.Who}'s main
        branch and pushed. Another Claude session wrote it; it builds. Read SPEC.md and any files you need for context.

        The request was: {request}

        Check that:
        - it does what was asked, and nothing unrelated;
        - it will work: logic bugs, WPF threading (UI only on the dispatcher), nulls, and exceptions that could crash
          Vex or leave him stuck;
        - it keeps SPEC.md section 2's principles (nothing new leaves the machine, exclusions respected, out of the way);
        - it's commented well enough for {Hub.Who} to follow later, and SPEC.md is updated if behaviour changed;
        - there are no secrets, personal details or machine-specific paths in the code.

        Approve unless there's a real problem. List only things that must change as issues; put nitpicks in notes.

        The staged diff{(diff.Length > 80_000 ? " (cut short; open the files for the rest)" : "")}:
        {(diff.Length > 80_000 ? diff[..80_000] : diff)}
        """;

    record Reply(string? Session, JsonElement? Json, string? Error);

    static async Task<Reply> Claude(string[] extra, string prompt, string model, string? resume, CancellationToken ct)
    {
        var exe = ClaudeCodeBrain.FindClaude() ?? throw new InvalidOperationException("Claude Code not found.");
        var args = new List<string> { "-p", "--output-format", "json", "--setting-sources", "", "--strict-mcp-config", "--model", model };
        args.AddRange(extra);
        if (resume != null) args.AddRange(new[] { "--resume", resume });
        var r = await Exec(exe, args, prompt, ct);
        try
        {
            using var doc = JsonDocument.Parse(r.Out);
            var root = doc.RootElement;
            var session = Field(root, "session_id");
            Note($"Claude ({model}): {Field(root, "result")}");
            if (root.TryGetProperty("is_error", out var e) && e.GetBoolean()) return new(session, null, Field(root, "result") ?? "error");
            return root.TryGetProperty("structured_output", out var so) && so.ValueKind == JsonValueKind.Object
                ? new(session, so.Clone(), null)
                : new(session, null, "no structured answer");
        }
        catch (JsonException) { Note("Unexpected Claude output:\n" + Tail(r.Out, 2000)); return new(null, null, "unreadable reply"); }
    }

    // ───────────── processes ─────────────

    /// <summary>The same publish as build.ps1, but into the staging folder and without stopping the running Vex.</summary>
    static async Task<(bool Ok, string Out)> Publish(CancellationToken ct)
    {
        if (Directory.Exists(OutDir)) try { Directory.Delete(OutDir, true); } catch { }
        var r = await Exec("dotnet", new[] { "publish", Path.Combine(Repo, "src", "Vex", "Vex.csproj"), "-c", "Release", "-r", "win-x64",
            "--self-contained", "true", "-p:PublishSingleFile=true", "-p:IncludeNativeLibrariesForSelfExtract=true",
            "-p:EnableCompressionInSingleFile=true", "-p:DebugType=none", "-o", OutDir }, null, ct);
        return (r.Code == 0 && File.Exists(StagedExe), r.Out);
    }

    static Task<(int Code, string Out)> Git(CancellationToken ct, params string[] args) => Git(ct, args, null);
    static async Task<(int Code, string Out)> Git(CancellationToken ct, string[] args, string? stdin)
    {
        var r = await Exec("git", args, stdin, ct);
        Note($"git {string.Join(' ', args)} → {r.Code}{(r.Out.Trim().Length > 0 && args[0] != "diff" ? "\n" + Tail(r.Out, 1500) : "")}");
        return r;
    }

    /// <summary>Runs a program in the repo folder with no window and no prompts, returning exit code and all output.</summary>
    static async Task<(int Code, string Out)> Exec(string exe, IEnumerable<string> args, string? stdin, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(exe)
        {
            WorkingDirectory = Repo, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false), StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        psi.Environment["GIT_TERMINAL_PROMPT"] = "0";   // fail rather than wait for a password nobody will type
        using var p = Process.Start(psi)!;
        if (stdin != null) await p.StandardInput.WriteAsync(stdin);
        p.StandardInput.Close();
        var outTask = p.StandardOutput.ReadToEndAsync(ct);
        var errTask = p.StandardError.ReadToEndAsync(ct);
        try { await p.WaitForExitAsync(ct); }
        catch (OperationCanceledException) { try { p.Kill(true); } catch { } throw; }
        return (p.ExitCode, await outTask + await errTask);
    }

    // ───────────── bits ─────────────

    static void Note(string text)
    {
        try { File.AppendAllText(log, $"[{DateTime.Now:HH:mm:ss}] {text}{Environment.NewLine}{Environment.NewLine}"); } catch { }
    }

    static string? Field(JsonElement? e, string name) =>
        e is { ValueKind: JsonValueKind.Object } o && o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    static string Slug(string s) =>
        string.Join('-', Regex.Matches(s.ToLowerInvariant(), "[a-z0-9]+").Select(m => m.Value).Take(5)) is { Length: > 0 } x ? x : "change";

    static string Tail(string s, int n) => s.Length <= n ? s.Trim() : "…" + s[^n..].Trim();

    static void Open(string path)
    {
        try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); } catch { }
    }
}
