using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;

namespace Vex.Services;

/// <summary>
/// What's kept in run.json about the current (or last) run, so the next start can tell how it ended.
/// </summary>
public class RunRecord
{
    public int Pid { get; set; }
    public string Exe { get; set; } = "";
    public DateTime Started { get; set; }
    /// <summary>Last heartbeat (once a minute): roughly when he was last known to be fine.</summary>
    public DateTime Alive { get; set; }
    /// <summary>Set on a clean exit (Quit, a workshop restart, Windows signing out or shutting down). Null = still running, or died.</summary>
    public DateTime? Ended { get; set; }
    public string EndReason { get; set; } = "";
    /// <summary>The fatal error that took him down, if one was caught on the way out ("" = none caught).</summary>
    public string Crash { get; set; } = "";
    public DateTime? CrashAt { get; set; }
    /// <summary>Resources at the last heartbeat, so a slow leak shows up in the crash log after the fact.</summary>
    public double MemoryMB { get; set; }
    public int Handles { get; set; }
    public int GdiObjects { get; set; }
    public int UserObjects { get; set; }
    /// <summary>When the watchdog last restarted him, kept across runs so a crash loop gives up instead of spinning.</summary>
    public List<DateTime> Restarts { get; set; } = new();
}

/// <summary>
/// Keeping Vex up, and saying so when he wasn't. Added after he vanished unnoticed on 21 and 22 Oct 2026 with nothing
/// in vex.log to say why:
///   - every unhandled error (UI thread, background threads, tasks, the tray menu) goes into a rotating crash.log
///     (Setup → Privacy → Crash log) before anything else happens;
///   - run.json is a heartbeat plus a clean-exit marker, so the next start knows whether the last run ended properly,
///     when it was last alive and what it was using (memory, handles) at the time;
///   - a small watchdog (a second, windowless copy of Vex.exe started with --watchdog) waits for him to exit and
///     restarts him if it wasn't a clean exit, backing off and giving up if he keeps falling over;
///   - back after an unclean end, he says so in a short bubble and puts a line in today's journal.
/// Full Vex only. Everything here stays in %LOCALAPPDATA%\Vex; the journal line only says what kind of ending it was.
/// </summary>
public static class Lifeline
{
    static VexConfig C => Store.Config;
    static string RunPath => Path.Combine(Store.Dir, "run.json");
    /// <summary>The crash log Setup's "Crash log" button opens. Older ones are crash.1.log, crash.2.log, ...</summary>
    public static string CrashLogPath => Path.Combine(Store.Dir, "crash.log");
    /// <summary>build.ps1 leaves this when it stops Vex to rebuild him, so the next start can say why he was gone.</summary>
    static string StoppedByPath => Path.Combine(Store.Dir, "stopped-by.txt");
    static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    static readonly object gate = new();

    static RunRecord? run;
    static Process? watchdog;
    static DateTime nextBeat, nextHealth;

    /// <summary>How the previous run ended, worked out at start; null if it ended cleanly (or there wasn't one).</summary>
    static (DateTime Lost, string Kind, string Reason, TimeSpan Down)? missing;
    static bool restartedByWatchdog;

    // ───────────── catching errors ─────────────

    /// <summary>
    /// Hooks every place an error can escape to. UI-thread errors are logged and survived, as before, unless they show
    /// WPF's rendering has died or keep coming (then he exits, uncleanly, so the watchdog brings back a working copy).
    /// Background errors can't be survived (.NET ends the process), so they're logged on the way out.
    /// </summary>
    public static void Install(Application app)
    {
        app.DispatcherUnhandledException += (_, e) => { UiError(e.Exception); e.Handled = true; };
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            Fatal("background thread", e.ExceptionObject as Exception ?? new Exception(e.ExceptionObject?.ToString()));
        // Fire-and-forget tasks (Claude calls, the workshop, traffic) don't crash .NET when they fail, but they did
        // fail silently; log them so there's a trail.
        TaskScheduler.UnobservedTaskException += (_, e) => { Error("background task", e.Exception); e.SetObserved(); };
        // The tray icon's WinForms menu has its own route for errors, which would otherwise pop up WinForms' error dialog.
        System.Windows.Forms.Application.ThreadException += (_, e) => Error("tray menu", e.Exception);
        // Signing out or shutting down isn't going missing: mark it clean and save the day so far.
        app.SessionEnding += (_, e) =>
        {
            Ended(e.ReasonSessionEnding == ReasonSessionEnding.Logoff ? "Windows sign-out" : "Windows shutdown");
            try { Hub.Stop(); Store.SaveState(); } catch { }
        };
    }

    static readonly Queue<DateTime> uiErrors = new();

    static void UiError(Exception ex)
    {
        var now = DateTime.Now;
        uiErrors.Enqueue(now);
        while (uiErrors.Peek() < now.AddMinutes(-1)) uiErrors.Dequeue();
        // A dead render thread (e.g. after a remote desktop or display change) leaves him invisible and frozen for
        // good, and a flood of errors means something's stuck failing every frame. Either way a fresh start is the fix.
        if (RenderThreadDied(ex) || uiErrors.Count > C.UiErrorsPerMinute)
        {
            Fatal(RenderThreadDied(ex) ? "UI thread (rendering failed)" : $"UI thread (over {C.UiErrorsPerMinute} errors a minute)", ex);
            try { Hub.Stop(); Store.SaveState(); } catch { }
            Environment.Exit(1);
        }
        Error("UI thread", ex);
    }

    /// <summary>WPF's "UCEERR_RENDERTHREADFAILURE" (0x88980406): its render thread is gone and won't come back.</summary>
    static bool RenderThreadDied(Exception? ex)
    {
        for (; ex != null; ex = ex.InnerException)
            if (unchecked((uint)ex.HResult) == 0x88980406 || ex.Message.Contains("UCEERR_RENDERTHREADFAILURE")) return true;
        return false;
    }

    static string lastError = "";
    static DateTime lastErrorAt;
    static int repeats;

    /// <summary>A survivable error: logged in full, but the same one over and over is logged once a minute with a count.</summary>
    static void Error(string where, Exception ex)
    {
        lock (gate)
        {
            var key = where + ex.GetType().FullName + ex.Message;
            if (key == lastError && DateTime.Now - lastErrorAt < TimeSpan.FromMinutes(1)) { repeats++; return; }
            var again = repeats > 0 ? $" (the previous error repeated {repeats} more times)" : "";
            lastError = key; lastErrorAt = DateTime.Now; repeats = 0;
            CrashLog($"Error on the {where}, carried on{again}:\n{ex}");
        }
        Store.Log($"Unhandled ({where}): {ex.GetType().Name}: {ex.Message}");
    }

    /// <summary>An error that's ending the process: logged, and noted in run.json so the next start can say what happened.</summary>
    static void Fatal(string where, Exception ex)
    {
        CrashLog($"CRASH on the {where}; Vex is exiting:\n{ex}");
        Store.Log($"Crash ({where}): {ex.GetType().Name}: {ex.Message}");
        lock (gate)
        {
            if (run == null) return;
            run.Crash = $"{ex.GetType().Name} on the {where}: {FirstLine(ex.Message)}";
            run.CrashAt = DateTime.Now;
            WriteRun(run);
        }
    }

    static string FirstLine(string s) { var l = s.Split('\n')[0].Trim(); return l.Length > 200 ? l[..200] + "…" : l; }

    /// <summary>Appends a timestamped entry to crash.log, starting a new file when it's grown past CrashLogKB.</summary>
    public static void CrashLog(string text)
    {
        lock (gate)
        {
            try
            {
                Directory.CreateDirectory(Store.Dir);
                var f = new FileInfo(CrashLogPath);
                if (f.Exists && f.Length > C.CrashLogKB * 1024L) Rotate();
                File.AppendAllText(CrashLogPath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  {text}{Environment.NewLine}");
            }
            catch { }   // nowhere left to report a failure to log
        }
    }

    static string OldLog(int n) => Path.Combine(Store.Dir, $"crash.{n}.log");

    static void Rotate()
    {
        int keep = Math.Max(1, C.CrashLogKeep);
        if (File.Exists(OldLog(keep))) File.Delete(OldLog(keep));
        for (int n = keep - 1; n >= 1; n--) if (File.Exists(OldLog(n))) File.Move(OldLog(n), OldLog(n + 1));
        File.Move(CrashLogPath, OldLog(1));
    }

    /// <summary>Setup → Privacy → Crash log: opens it, starting an empty one if nothing's gone wrong yet.</summary>
    public static void OpenCrashLog()
    {
        if (!File.Exists(CrashLogPath)) CrashLog("Crash log started. Nothing has gone wrong yet.");
        App.Open(CrashLogPath);
    }

    // ───────────── the run marker ─────────────

    /// <summary>
    /// At start (after the single-instance check): reads how the last run ended, notes an unclean one in crash.log,
    /// then starts this run's record and its watchdog.
    /// </summary>
    public static void Begin(bool restarted)
    {
        restartedByWatchdog = restarted;
        var prev = ReadRun();
        var stoppedBy = TakeStoppedBy(prev);
        var now = DateTime.Now;
        if (prev != null && prev.Ended == null)
        {
            var lost = prev.CrashAt ?? prev.Alive;
            var boot = now - TimeSpan.FromMilliseconds(Environment.TickCount64);
            var (kind, reason) =
                prev.Crash.Length > 0 ? ("crash", prev.Crash)
                : stoppedBy != null ? ("build", $"stopped by {stoppedBy}")
                : boot > prev.Alive ? ("reboot", "the PC restarted or lost power without signing out (an update, a crash, or the power)")
                : ("unknown", "no error was caught: ended from outside (Task Manager, a script), or a crash .NET can't report");
            missing = (lost, kind, reason, now - lost);
            CrashLog($"Back after an unclean exit. Previous run (pid {prev.Pid}, started {prev.Started:yyyy-MM-dd HH:mm}) was last " +
                     $"seen {lost:yyyy-MM-dd HH:mm:ss}, using {prev.MemoryMB:0} MB, {prev.Handles} handles, {prev.GdiObjects} GDI and " +
                     $"{prev.UserObjects} USER objects. Why: {reason}.{(restarted ? " Restarted by the watchdog." : "")}");
        }
        else if (prev?.Ended is { } ended) Store.Log($"Started. Last run ended cleanly ({prev.EndReason}, {ended:yyyy-MM-dd HH:mm}).");

        lock (gate)
        {
            run = new RunRecord
            {
                Pid = Environment.ProcessId, Exe = Environment.ProcessPath ?? "", Started = now, Alive = now,
                // Carried forward so the back-off still knows about recent restarts.
                Restarts = prev?.Restarts.Where(t => now - t < TimeSpan.FromMinutes(C.WatchdogWindowMinutes)).ToList() ?? new(),
            };
            Sample(run);
            WriteRun(run);
        }
        StartWatchdog();
    }

    /// <summary>The build script's note, if it was left since the last heartbeat (an older one is stale and ignored).</summary>
    static string? TakeStoppedBy(RunRecord? prev)
    {
        try
        {
            if (!File.Exists(StoppedByPath)) return null;
            var by = File.ReadAllText(StoppedByPath).Trim();
            var at = File.GetLastWriteTime(StoppedByPath);
            File.Delete(StoppedByPath);
            return prev != null && at >= prev.Alive.AddMinutes(-2) && by.Length > 0 ? by : null;
        }
        catch { return null; }
    }

    /// <summary>A clean exit: the next start won't say he went missing, and the watchdog stands down.</summary>
    public static void Ended(string reason)
    {
        lock (gate)
        {
            if (run == null) return;
            run.Ended = DateTime.Now; run.EndReason = reason;
            WriteRun(run);
        }
        StopWatchdog();
    }

    /// <summary>Every 15 s from the hub: the heartbeat once a minute, a health line in vex.log every HealthLogMinutes.</summary>
    public static void Tick()
    {
        var now = DateTime.Now;
        if (now < nextBeat) return;
        nextBeat = now.AddMinutes(1);
        lock (gate)
        {
            if (run == null) return;
            // Signing out was cancelled after we'd marked the run as over (another app refused): still here, so undo that.
            if (run.Ended is { } ended && run.EndReason.StartsWith("Windows") && now - ended > TimeSpan.FromMinutes(2))
            {
                run.Ended = null; run.EndReason = "";
                StartWatchdog();
            }
            run.Alive = now;
            Sample(run);
            WriteRun(run);
            if (now >= nextHealth && C.HealthLogMinutes > 0)
            {
                nextHealth = now.AddMinutes(C.HealthLogMinutes);
                Store.Log($"Health: {run.MemoryMB:0} MB, {run.Handles} handles, {run.GdiObjects} GDI, {run.UserObjects} USER objects, up since {run.Started:yyyy-MM-dd HH:mm}");
            }
        }
    }

    [DllImport("user32.dll")] static extern uint GetGuiResources(IntPtr hProcess, uint flags);

    /// <summary>Memory and handle counts now. GDI and USER objects run out at 10,000 per process, which kills WPF apps.</summary>
    static void Sample(RunRecord r)
    {
        try
        {
            using var me = Process.GetCurrentProcess();
            r.MemoryMB = me.WorkingSet64 / 1048576.0;
            r.Handles = me.HandleCount;
            r.GdiObjects = (int)GetGuiResources(me.Handle, 0);
            r.UserObjects = (int)GetGuiResources(me.Handle, 1);
        }
        catch { }
    }

    static RunRecord? ReadRun()
    {
        try { return File.Exists(RunPath) ? JsonSerializer.Deserialize<RunRecord>(File.ReadAllText(RunPath)) : null; }
        catch (Exception ex) { Store.Log($"Couldn't read run.json: {ex.Message}"); return null; }
    }

    static void WriteRun(RunRecord r)
    {
        try
        {
            Directory.CreateDirectory(Store.Dir);
            var tmp = RunPath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(r, Json));
            File.Move(tmp, RunPath, overwrite: true);
        }
        catch { }   // the heartbeat is best effort; it'll try again in a minute
    }

    // ───────────── back after going missing ─────────────

    /// <summary>
    /// From the hub's start: if the last run didn't end cleanly, a short bubble saying so, and a line in today's
    /// journal with when and what kind of ending it was. The full error stays in crash.log; the journal is part of what
    /// goes to Claude when chatting, so it only gets the kind (and the error's type name), never the details.
    /// </summary>
    public static void Greet()
    {
        if (missing is not { } m) return;
        var when = When(m.Lost);
        Hub.Vex.Say(m.Kind switch
        {
            "crash" => $"I fell over around {when}. Back now. It's in the crash log.",
            "build" => $"Someone rebuilt me around {when}. Back now.",
            "reboot" => $"I went missing around {when}. So did the PC, to be fair. Back now.",
            _ => $"I went missing around {when}. Back now.",
        }, 8, important: true);

        var why = m.Kind switch
        {
            "crash" => $"crashed ({m.Reason.Split(' ')[0]}; details in crash.log)",
            "build" => "stopped for a rebuild",
            "reboot" => "the PC restarted without signing out",
            _ => "reason unknown; nothing was caught on the way out",
        };
        var line = $"Vex was down from {when} to {DateTime.Now:HH:mm} ({Span(m.Down)}): {why}{(restartedByWatchdog ? ", restarted by his watchdog" : "")}.";
        try { if (Hub.Notes.Linked) Hub.Notes.AppendJournal(line, quiet: true); }
        catch (Exception ex) { Store.Log($"Couldn't note the downtime in the journal: {ex.Message}"); }
        missing = null;
    }

    static string When(DateTime t) =>
        t.Date == DateTime.Today ? $"{t:HH:mm}" : t.Date == DateTime.Today.AddDays(-1) ? $"{t:HH:mm} yesterday" : $"{t:ddd d MMM HH:mm}";

    static string Span(TimeSpan d) =>
        d.TotalMinutes < 1 ? "under a minute" : d.TotalHours < 1 ? $"{(int)d.TotalMinutes} min" : $"{(int)d.TotalHours}h{d.Minutes:00}";

    // ───────────── the watchdog ─────────────

    /// <summary>Starts this run's watchdog: a second, windowless copy of Vex.exe that waits for this one to exit.</summary>
    static void StartWatchdog()
    {
        if (!C.Watchdog || watchdog is { HasExited: false } || Environment.ProcessPath is not { } exe) return;
        try
        {
            var psi = new ProcessStartInfo(exe) { UseShellExecute = false, CreateNoWindow = true };
            psi.ArgumentList.Add("--watchdog");
            psi.ArgumentList.Add(Environment.ProcessId.ToString());
            watchdog = Process.Start(psi);
        }
        catch (Exception ex) { Store.Log($"Watchdog didn't start: {ex.Message}"); }
    }

    /// <summary>
    /// A clean exit doesn't need watching. Stopped here rather than left to notice by itself, so it's gone before a
    /// workshop swap tries to overwrite Vex.exe (which a running watchdog would hold open).
    /// </summary>
    static void StopWatchdog()
    {
        try { if (watchdog is { HasExited: false }) watchdog.Kill(); } catch { }
        watchdog = null;
    }

    /// <summary>
    /// The watchdog process itself (Vex.exe --watchdog &lt;pid&gt;, see App.OnStartup): waits for that Vex to exit and,
    /// unless it was a clean exit, starts him again after WatchdogDelaySeconds, doubling the wait for each restart in
    /// the last WatchdogWindowMinutes and giving up after WatchdogMaxRestarts of them, so a crash loop can't spin.
    /// </summary>
    public static void Watch(int pid)
    {
        Process vex;
        try { vex = Process.GetProcessById(pid); } catch { return; }
        vex.WaitForExit();

        var last = ReadRun();
        if (last == null || last.Pid != pid || last.Ended != null || !C.Watchdog) return;   // a clean exit, or not ours to restart
        var now = DateTime.Now;
        var recent = last.Restarts.Where(t => now - t < TimeSpan.FromMinutes(C.WatchdogWindowMinutes)).ToList();
        if (recent.Count >= C.WatchdogMaxRestarts)
        {
            CrashLog($"Watchdog: Vex (pid {pid}) exited uncleanly, but he's been restarted {recent.Count} times in the last " +
                     $"{C.WatchdogWindowMinutes:0} minutes, so I'm leaving him down. Start Vex.exe by hand once it's sorted.");
            return;
        }
        Thread.Sleep(TimeSpan.FromSeconds(C.WatchdogDelaySeconds * Math.Pow(2, recent.Count)));

        // Someone (build.ps1, a workshop swap, Ben) may have started him again meanwhile.
        if (Mutex.TryOpenExisting(App.MutexName, out var other)) { other.Dispose(); return; }
        var exe = File.Exists(last.Exe) ? last.Exe : Environment.ProcessPath!;
        recent.Add(DateTime.Now);
        last.Restarts = recent;
        WriteRun(last);
        CrashLog($"Watchdog: Vex (pid {pid}) exited uncleanly; restarting him (restart {recent.Count} of at most {C.WatchdogMaxRestarts} in {C.WatchdogWindowMinutes:0} min).");
        try
        {
            var psi = new ProcessStartInfo(exe) { UseShellExecute = false };
            psi.ArgumentList.Add("--restarted");
            Process.Start(psi);
        }
        catch (Exception ex) { CrashLog($"Watchdog: couldn't restart Vex: {ex.Message}"); }
    }
}
