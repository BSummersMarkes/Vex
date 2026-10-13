using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;

namespace Vex.Services;

public class Nudge
{
    public string Type { get; init; } = "";
    public string Text { get; init; } = "";
    /// <summary>Meetings, Claude Code and reminders: skip the rate limit and quiet times.</summary>
    public bool Exempt { get; init; }
    public double Seconds { get; init; } = 25;
    public List<(string Label, Action Act)> Buttons { get; } = new();
    /// <summary>Called if the nudge is replaced before being answered (counts as "ignored").</summary>
    public Action? OnTimeout { get; init; }
    /// <summary>Stay up until a button is pressed (the default). Otherwise it fades after <see cref="Seconds"/>.</summary>
    public bool Persistent { get; init; } = true;
    /// <summary>Checked continually; when it turns false the bubble quietly goes (e.g. Claude was answered).</summary>
    public Func<bool>? StillValid { get; init; }
}

/// <summary>
/// Decides when Vex pipes up. One wellbeing nudge per gap at most; never while away, on a break, in a
/// call or full screen. Learns per nudge type: brushed off often → asks less often, acted on → back to normal.
/// Exempt nudges (reminders, meetings, Claude Code, packing up) that can't be shown yet (something's full screen)
/// stay pending and are retried every tick, rather than dropped.
/// </summary>
public class Nudges
{
    readonly Dictionary<string, DateTime> last = new();
    readonly HashSet<string> meetingKeys = new();
    DateTime lastNormal = DateTime.MinValue;

    VexConfig C => Store.Config;

    // ───────────── showing ─────────────

    /// <summary>The last exempt nudge put on screen, and when.</summary>
    Nudge? lastExempt;
    DateTime lastExemptAt = DateTime.MinValue;

    public bool Offer(Nudge n)
    {
        if (!n.Exempt)
        {
            if (Hub.Quiet || Hub.Presence.Away) return false;
            if (DateTime.Now - lastNormal < TimeSpan.FromMinutes(C.NudgeGapMinutes)) return false;
            lastNormal = DateTime.Now;
        }
        else
        {
            if (Hub.FullScreen) return false;
            // One exempt nudge at a time: if another is still up (unanswered, still relevant, under 2 minutes old),
            // this one waits for a later tick instead of replacing it, so a backlog after full screen comes out in turn.
            if (lastExempt != null && Hub.Vex.ShowingNudge(lastExempt) && lastExempt.StillValid?.Invoke() != false
                && DateTime.Now - lastExemptAt < TimeSpan.FromMinutes(2)) return false;
            lastExempt = n; lastExemptAt = DateTime.Now;
        }
        last[n.Type] = DateTime.Now;
        Hub.Vex.ShowNudge(n);
        return true;
    }

    /// <summary>A wellbeing-type nudge would be shown now (at the desk, not quiet, gap passed).</summary>
    public bool CanNudge =>
        !Hub.Quiet && !Hub.Presence.Away && !Hub.Presence.OnBreak && DateTime.Now - lastNormal >= TimeSpan.FromMinutes(C.NudgeGapMinutes);

    NudgeStat Stat(string type)
    {
        if (!Store.State.NudgeStats.TryGetValue(type, out var s)) Store.State.NudgeStats[type] = s = new NudgeStat();
        return s;
    }

    void Acted(string type) { var s = Stat(type); s.Acted++; s.Factor = Math.Max(0.8, s.Factor * 0.9); Store.SaveState(); }
    void Dismissed(string type) { var s = Stat(type); s.Dismissed++; s.Factor = Math.Min(2.5, s.Factor * 1.25); Store.SaveState(); }
    void Ignored(string type) { var s = Stat(type); s.Ignored++; s.Factor = Math.Min(2.5, s.Factor * 1.08); Store.SaveState(); }

    TimeSpan Every(string type, double minutes) => TimeSpan.FromMinutes(minutes * Stat(type).Factor);

    DateTime Last(string type) => last.TryGetValue(type, out var t) ? t : DateTime.MinValue;

    Nudge Wellbeing(string type, string text, string doneLabel = "Done", double snoozeMinutes = 10, Action? onDone = null)
    {
        var n = new Nudge { Type = type, Text = text, OnTimeout = () => Ignored(type) };
        n.Buttons.Add((doneLabel, () => { Acted(type); onDone?.Invoke(); }));
        n.Buttons.Add(("Later", () => last[type] = DateTime.Now - Every(type, Interval(type)) + TimeSpan.FromMinutes(snoozeMinutes)));
        n.Buttons.Add(("✕", () => Dismissed(type)));
        return n;
    }

    double Interval(string type) => type switch
    {
        "eyes" => C.EyesEveryMinutes,
        "stretch" => C.StretchEveryMinutes,
        "water" => C.WaterEveryMinutes,
        "focus" => C.FocusCheckMinutes,
        "curious" => C.CuriousEveryMinutes,
        _ => 60,
    };

    // ───────────── the rules ─────────────

    public void Tick()
    {
        var now = DateTime.Now;
        Reminders(now);
        Meetings(now);
        ClaudeRenudge(now);
        PackUp(now);

        var p = Hub.Presence;
        if (p.Away || p.OnBreak || Hub.Quiet) return;
        if (now - lastNormal < TimeSpan.FromMinutes(C.NudgeGapMinutes)) return;

        foreach (var make in new Func<DateTime, Nudge?>[] { Morning, Lunch, Stretch, Eyes, Water, Focus, EndOfDay })
            if (make(now) is { } n && Offer(n)) return;
    }

    /// <summary>
    /// First sight of you in the morning: meetings and open to-dos in a line, built locally (no Claude call).
    /// "Rundown" opens the chat for the full version, with yesterday's diary to draw on.
    /// </summary>
    Nudge? Morning(DateTime now)
    {
        if (!C.MorningBrief || Store.State.DoneToday.Contains("morning")) return null;
        var first = Hub.Presence.FirstSeenToday;
        if (first == DateTime.MaxValue || now.Hour < 6) return null;
        MarkDone("morning");
        if (now - first > TimeSpan.FromHours(3)) return null;   // started Vex mid-day: too late for "morning"
        var meetings = Hub.Calendar.Upcoming.ToList();
        var todos = Hub.Notes.Linked ? Hub.Notes.OpenChecks.Count + Hub.Notes.Open.Count() : 0;
        var carried = Hub.Notes.Linked ? Hub.Notes.OpenChecks.Count(c => c.Date != Store.Today) : 0;
        var parts = new List<string>();
        if (Hub.Calendar.Source != "none")
            parts.Add(meetings.Count == 0 ? "No meetings." : $"{meetings.Count} meeting{(meetings.Count == 1 ? "" : "s")}, first {meetings[0].Subject} at {meetings[0].Start:HH:mm}.");
        if (Hub.Notes.Linked)
            parts.Add(todos == 0 ? "Nothing open on the to-do front." : $"{todos} to-do{(todos == 1 ? "" : "s")} open{(carried > 0 ? $", {carried} carried over" : "")}.");
        var n = new Nudge { Type = "morning", Text = "Morning. " + (parts.Count > 0 ? string.Join(" ", parts) : "Want the rundown?") };
        n.Buttons.Add(("Rundown", () => Hub.Vex.OpenChat(
            "Morning rundown: what's on today, what carried over from yesterday (use your diary), and what should I start with? Keep it short.")));
        n.Buttons.Add(("✕", () => { }));
        return n;
    }

    /// <summary>A question Vex came up with himself (<see cref="Curiosity"/>). "Tell you" opens the chat with it.</summary>
    public Nudge Curious(string question) =>
        Wellbeing("curious", question, doneLabel: "Tell you", snoozeMinutes: 30, onDone: () => Hub.Vex.AskInChat(question));

    DateTime SinceSitting(string type) => Last(type) > Hub.Presence.SittingSince ? Last(type) : Hub.Presence.SittingSince;

    /// <summary>When a due stretch nudge was first held back for a remote session (<see cref="VexConfig.StretchHoldApps"/>).</summary>
    DateTime stretchHeldSince = DateTime.MinValue;

    Nudge? Stretch(DateTime now)
    {
        var since = SinceSitting("stretch");
        if (now - since < Every("stretch", C.StretchEveryMinutes)) { stretchHeldSince = DateTime.MinValue; return null; }
        // Watching a remote test run (TeamViewer, remote desktop): don't barge in. It waits until you switch to
        // another window, but only up to StretchHoldMaxMinutes, so it can't be put off forever.
        if (C.StretchHoldApps.Any(a => a.Equals(Hub.Activity.CurrentApp, StringComparison.OrdinalIgnoreCase)))
        {
            if (stretchHeldSince == DateTime.MinValue) stretchHeldSince = now;
            if (now - stretchHeldSince < TimeSpan.FromMinutes(C.StretchHoldMaxMinutes)) return null;
        }
        stretchHeldSince = DateTime.MinValue;
        var mins = (int)(now - Hub.Presence.SittingSince).TotalMinutes;
        return Wellbeing("stretch", $"You've been sat for {mins} minutes. Stand up, stretch. I'll guard the desk.",
                         onDone: () => last["eyes"] = DateTime.Now);
    }

    Nudge? Eyes(DateTime now)
    {
        if (now - SinceSitting("eyes") < Every("eyes", C.EyesEveryMinutes)) return null;
        return Wellbeing("eyes", Pick("20-20-20: look at something 20 feet away for 20 seconds. I'll wait.",
                                      "Eyes off the screen for 20 seconds. Window, wall, anything far away.",
                                      "Blink. Look away. Twenty seconds. Screens aren't going anywhere."));
    }

    Nudge? Water(DateTime now)
    {
        var since = Last("water") > Hub.Presence.FirstSeenToday ? Last("water") : Hub.Presence.FirstSeenToday;
        if (now - since < Every("water", C.WaterEveryMinutes)) return null;
        return Wellbeing("water", Store.State.Coffees >= 2
            ? $"{Store.State.Coffees} coffees and, I suspect, no water. Fix one of those."
            : "Water. The boring drink. Have some.", doneLabel: "Drinking");
    }

    Nudge? Focus(DateTime now)
    {
        var app = Hub.Activity.CurrentApp;
        if (app is "" or Activity.Away or Activity.Private) return null;
        var window = TimeSpan.FromMinutes(C.FocusCheckMinutes);
        if (now - Last("focus") < Every("focus", C.FocusCheckMinutes)) return null;
        if (now - Hub.Presence.FirstSeenToday < window) return null;
        if (Hub.Activity.MinutesIn(app, now - window) < window.TotalMinutes * 0.8) return null;
        var hours = window.TotalHours;
        return Wellbeing("focus", $"That's {(hours == 2 ? "two hours" : $"{hours:0.#} hours")} mostly in {app}. Still the plan?",
                         doneLabel: "Yes", snoozeMinutes: 30);
    }

    Nudge? Lunch(DateTime now)
    {
        if (Store.State.DoneToday.Contains("lunch")) return null;
        if (!TimeSpan.TryParse(C.LunchBy, out var by) || now.TimeOfDay < by) return null;
        if (Hub.Presence.HadBreakSince(now.Date.AddHours(11.5), TimeSpan.FromMinutes(20))) { MarkDone("lunch"); return null; }
        var n = new Nudge { Type = "lunch", Text = "No lunch break yet. Food is a thing. Go and have some." };
        n.Buttons.Add(("Going", () => { Acted("lunch"); MarkDone("lunch"); Hub.Vex.StartLunch(); }));
        n.Buttons.Add(("Later", () => last["lunch"] = DateTime.Now));
        n.Buttons.Add(("✕", () => { Dismissed("lunch"); MarkDone("lunch"); }));
        return now - Last("lunch") < TimeSpan.FromMinutes(25) ? null : n;
    }

    Nudge? EndOfDay(DateTime now)
    {
        if (Store.State.DoneToday.Contains("eod")) return null;
        if (!TimeSpan.TryParse(C.EndOfDay, out var at) || now.TimeOfDay < at) return null;
        MarkDone("eod");
        int open = Hub.Notes.OpenChecks.Count(c => c.Date == Store.Today) + Hub.Notes.Open.Count();
        var n = new Nudge
        {
            Type = "eod", Seconds = 60,
            Text = open > 0 ? $"End of day-ish. {open} to-do{(open == 1 ? "" : "s")} still open. Want a summary?"
                            : "End of day-ish. Want a summary of today?",
        };
        n.Buttons.Add(("Summarise", () => Hub.Vex.OpenChat(
            "Summarise my day from what you've seen: what I worked on, meetings, and what's still open. " +
            "Then suggest the first thing for tomorrow. Offer to add the summary to my journal.")));
        n.Buttons.Add(("✕", () => { }));
        return n;
    }

    static void MarkDone(string what) { Store.State.DoneToday.Add(what); Store.SaveState(); }

    // ───────────── exempt: reminders, meetings, Claude Code ─────────────

    void Reminders(DateTime now)
    {
        var due = Store.State.Reminders.Where(r => r.At <= now).ToList();
        if (due.Count == 0) return;
        foreach (var r in due)
        {
            var n = new Nudge { Type = "reminder", Exempt = true, Seconds = 120, Text = $"Reminder: {r.Text}" };
            n.Buttons.Add(("OK", () => { }));
            n.Buttons.Add(("10 min", () => { Store.State.Reminders.Add(new Reminder { At = DateTime.Now.AddMinutes(10), Text = r.Text }); Store.SaveState(); }));
            // Kept until it's actually on screen: while something's full screen it waits for the next tick.
            if (!Offer(n)) return;
            Store.State.Reminders.Remove(r);
            Store.SaveState();
        }
    }

    void Meetings(DateTime now)
    {
        var warns = C.MeetingWarnMinutes.OrderBy(w => w).ToArray();
        foreach (var m in Hub.Calendar.Upcoming.Take(5))
        {
            var until = (m.Start - now).TotalMinutes;
            var id = $"{m.Start:HHmm}|{m.Subject}";
            if (until > 0)
            {
                // The tightest warning window we're inside; mark the wider ones as done so they don't double up.
                var w = warns.FirstOrDefault(x => until <= x);
                if (w == 0 || meetingKeys.Contains($"{id}|{w}")) continue;
                var where = string.IsNullOrWhiteSpace(m.Location) ? "" : $" ({m.Location})";
                var n = new Nudge
                {
                    Type = "meeting", Exempt = true, Seconds = 90, StillValid = () => DateTime.Now < m.End,
                    Text = until < 1.5 ? $"{m.Subject}{where} is starting now." : $"{m.Subject}{where} in {Math.Ceiling(until)} minutes.",
                };
                n.Buttons.Add(("OK", () => { }));
                if (until >= 3) n.Buttons.Add(("Prep me", () => Hub.Vex.OpenChat(
                    $"I've got \"{m.Subject}\" at {m.Start:HH:mm}. Anything I should know or have ready? Check your pages, my journal and to-dos. Brief.")));
                // Only counts as warned once it's on screen; until then it's retried (and stops when the meeting ends).
                if (!Offer(n)) continue;
                meetingKeys.Add($"{id}|{w}");
                foreach (var wider in warns.Where(x => x > w)) meetingKeys.Add($"{id}|{wider}");
            }
            else if (until <= -2 && until > -10 && Activity.MicInUse() == null && !Hub.Presence.Away && !meetingKeys.Contains($"{id}|late"))
            {
                var n = new Nudge { Type = "meeting", Exempt = true, Seconds = 60, Text = $"Aren't you meant to be in {m.Subject}?" };
                n.Buttons.Add(("On it", () => { }));
                n.Buttons.Add(("Skipping it", () => { }));
                if (Offer(n)) meetingKeys.Add($"{id}|late");
            }
        }
    }

    public void OnClaude(ClaudeSession s)
    {
        if (s.State == ClaudeState.NeedsYou)
        {
            // Not shown (full screen)? LastNudge stays old, so ClaudeRenudge tries again on the next tick.
            if (Offer(ClaudeNudge(s, $"{s.Project}: {s.Message ?? "Claude needs you."}", ClaudeState.NeedsYou))) s.LastNudge = DateTime.Now;
        }
        else if (s.State == ClaudeState.Finished && s.WorkedFor >= TimeSpan.FromSeconds(20) && !VsCodeInFront())
        {
            Offer(ClaudeNudge(s, $"{s.Project}: Claude's finished.", ClaudeState.Finished));
        }
    }

    void ClaudeRenudge(DateTime now)
    {
        foreach (var s in Hub.Claude.Sessions.Values)
            if (s.State == ClaudeState.NeedsYou && now - s.LastNudge >= TimeSpan.FromMinutes(C.ClaudeRenudgeMinutes)
                && now - s.Since < TimeSpan.FromHours(1) && !Hub.Presence.Away)
            {
                var first = s.LastNudge < s.Since;
                var text = first ? $"{s.Project}: {s.Message ?? "Claude needs you."}"
                                 : $"Still waiting on you: {s.Project}. {(int)(now - s.Since).TotalMinutes} minutes and counting.";
                if (Offer(ClaudeNudge(s, text, ClaudeState.NeedsYou))) s.LastNudge = now;
            }
    }

    // ───────────── going home ─────────────

    bool packUpChecking;
    DateTime packUpSnooze = DateTime.MinValue;

    /// <summary>
    /// PackUpMinutes before LeaveAt: "pack up", with the drive home if TomTom is set up. Exempt (like a meeting), shown
    /// once a day; if it can't be shown yet it's retried until half an hour after leaving time. Also counts as the
    /// end-of-day nudge, which would otherwise come after you've gone.
    /// </summary>
    void PackUp(DateTime now)
    {
        if (packUpChecking || Store.State.DoneToday.Contains("packup") || now < packUpSnooze) return;
        if (Commute.PackUpAt() is not { } at || Commute.LeaveTime() is not { } leave) return;
        if (now < at || now > leave.AddMinutes(30) || Hub.Presence.Away) return;   // away: wait for you to be back (or gone)
        packUpChecking = true;
        _ = ShowPackUp(leave);
    }

    async System.Threading.Tasks.Task ShowPackUp(DateTime leave)
    {
        try
        {
            var traffic = await Commute.Check(maxAge: TimeSpan.FromMinutes(5));   // retries reuse a fresh result
            var n = new Nudge
            {
                Type = "packup", Exempt = true, Seconds = 90,
                Text = $"Pack-up time, you're off at {leave:HH:mm}." + (traffic != null ? " " + traffic : ""),
            };
            n.Buttons.Add(("Packing up", () => Acted("packup")));
            n.Buttons.Add(("Summarise day", () => Hub.Vex.OpenChat(
                "I'm packing up. Summarise my day from what you've seen: what I worked on, meetings, and what's still open. " +
                "Then suggest the first thing for tomorrow. Offer to add the summary to my journal.")));
            n.Buttons.Add(("Map", Commute.OpenMap));
            n.Buttons.Add(("10 min", () => { packUpSnooze = DateTime.Now.AddMinutes(10); Store.State.DoneToday.Remove("packup"); Store.SaveState(); }));
            if (Offer(n)) { MarkDone("packup"); if (!Store.State.DoneToday.Contains("eod")) MarkDone("eod"); }
        }
        catch (Exception ex) { Store.Log($"Pack-up: {ex.Message}"); }
        finally { packUpChecking = false; }
    }

    static Nudge ClaudeNudge(ClaudeSession s, string text, ClaudeState shownFor)
    {
        // Goes away by itself once the session moves on (e.g. you answered Claude in VS Code).
        var n = new Nudge { Type = "claude", Exempt = true, Seconds = 45, Text = text, StillValid = () => s.State == shownFor };
        n.Buttons.Add(("Show me", () => ClaudeWatch.FocusVsCode(s.Project)));
        n.Buttons.Add(("✕", () => { }));
        return n;
    }

    static bool VsCodeInFront()
    {
        var h = Native.GetForegroundWindow();
        Native.GetWindowThreadProcessId(h, out uint pid);
        try { return Process.GetProcessById((int)pid).ProcessName == "Code"; } catch { return false; }
    }

    static readonly Random rng = new();
    static string Pick(params string[] s) => s[rng.Next(s.Length)];
}
