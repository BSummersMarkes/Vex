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
    /// <summary>
    /// What's needed to remake the buttons if it goes unanswered and Vex restarts before it's picked from the chat's
    /// Pending tab (the reminder's text, the meeting's subject and start...). See <see cref="Nudges.Rebuild"/>.
    /// </summary>
    public Dictionary<string, string> Args { get; init; } = new();
    /// <summary>Unanswered nudges with the same key replace each other in Pending (one stretch, not five). Default: type + text.</summary>
    public string? PendingKey { get; init; }
    /// <summary>After this it's not worth offering from Pending any more (the meeting's over). Null = kept PendingKeepDays.</summary>
    public DateTime? Until { get; init; }
}

/// <summary>
/// Decides when Vex pipes up. One wellbeing nudge per gap at most; never while away, in a call or full screen.
/// Breaks and lunch are never assumed from time away: only the stretch and lunch nudges' buttons (or the chat) record one. Learns per nudge type: brushed off often → asks less often, acted on → back to normal.
/// Exempt nudges (reminders, meetings, Claude Code, packing up, clocking in/out on home days) that can't be shown yet (something's full screen)
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
            // Napping at lunch: only the important (exempt) ones wake him; the rest wait for their next turn.
            if (Hub.Quiet || Hub.Presence.Away || Hub.Vex.LunchNapping) return false;
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
        !Hub.Quiet && !Hub.Presence.Away && DateTime.Now - lastNormal >= TimeSpan.FromMinutes(C.NudgeGapMinutes);

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

    Nudge Wellbeing(string type, string text, string doneLabel = "Done", double snoozeMinutes = 10, Action? onDone = null, string? key = null)
    {
        var n = new Nudge { Type = type, Text = text, OnTimeout = () => Ignored(type), PendingKey = key ?? type };
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

    /// <summary>
    /// Remakes an unanswered nudge from the chat's Pending tab after a restart (the original's buttons went with the old
    /// process), so picking an option still does what it would have done at the time. Null for a type it can't remake.
    /// </summary>
    public Nudge? Rebuild(PendingItem p)
    {
        string Arg(string k) => p.Args.TryGetValue(k, out var v) ? v : "";
        DateTime When(string k) => DateTime.TryParse(Arg(k), null, System.Globalization.DateTimeStyles.RoundtripKind, out var t) ? t : DateTime.Now;
        return p.Type switch
        {
            "eyes" => Wellbeing("eyes", p.Text),
            "stretch" => StretchNudge(p.Text),
            "water" => WaterNudge(p.Text),
            "focus" => FocusNudge(p.Text),
#if !LITE
            "curious" => Curious(p.Text),
            "eod" => EndOfDayNudge(p.Text),
            "claude" => ClaudeNudge(Arg("project"), p.Text),
            "workshop" => Workshop.Notice(p.Options.Contains("Restart now"), p.Text, Arg("log")),
#endif
            "morning" => MorningNudge(p.Text),
            "lunch" => LunchNudge(p.Text),
            "reminder" => ReminderNudge(Arg("text"), Store.State.Reminders.Any(r => r.Id == Arg("repeat")) ? Arg("repeat") : null),
            "meeting" when p.Options.Contains("On it") => LateNudge(Arg("subject"), When("start"), When("end")),
            "meeting" => MeetingNudge(p.Text, Arg("subject"), When("start"), When("end"), prep: p.Options.Contains("Prep me")),
            "packup" => PackUpNudge(p.Text),
#if !LITE
            "clockin" => ClockInNudge(p.Text),
            "clockout" => ClockOutNudge(p.Text),
#endif
            _ => null,
        };
    }

    // ───────────── the rules ─────────────

    public void Tick()
    {
        var now = DateTime.Now;
        Reminders(now);
        Meetings(now);
#if !LITE
        ClaudeRenudge(now);
#endif
        PackUp(now);
#if !LITE
        // A clock-in/out bubble waiting for an answer isn't buried under a wellbeing nudge; those wait their turn.
        if (Clock(now) || (lastClock != null && Hub.Vex.ShowingNudge(lastClock))) return;
#endif

        var p = Hub.Presence;
        if (p.Away || Hub.Quiet || Hub.Vex.LunchNapping) return;   // (some mark themselves done once made, so don't make them now)
        if (now - lastNormal < TimeSpan.FromMinutes(C.NudgeGapMinutes)) return;

#if LITE
        foreach (var make in new Func<DateTime, Nudge?>[] { Morning, Lunch, Stretch, Eyes, Water, Focus })   // no to-dos, so no end-of-day round-up
#else
        foreach (var make in new Func<DateTime, Nudge?>[] { Morning, Lunch, Stretch, Eyes, Water, Focus, EndOfDay })
#endif
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
#if LITE
        var parts = new List<string>();
        if (Hub.Calendar.Source != "none")
            parts.Add(meetings.Count == 0 ? "No meetings." : $"{meetings.Count} meeting{(meetings.Count == 1 ? "" : "s")}, first {meetings[0].Subject} at {meetings[0].Start:HH:mm}.");
        var reminders = Store.State.Reminders.Where(r => r.At.Date == now.Date).OrderBy(r => r.At).ToList();
        if (reminders.Count > 0)
            parts.Add($"{reminders.Count} reminder{(reminders.Count == 1 ? "" : "s")} today, first at {reminders[0].At:HH:mm}: {reminders[0].Text}.");
        return MorningNudge("Morning. " + (parts.Count > 0 ? string.Join(" ", parts) : "Nothing on that I know of. Lovely."));
#else
        var todos = Hub.Notes.Linked ? Hub.Notes.OpenChecks.Count + Hub.Notes.Open.Count() : 0;
        var carried = Hub.Notes.Linked ? Hub.Notes.OpenChecks.Count(c => c.Date != Store.Today) : 0;
        var parts = new List<string>();
        if (Hub.Calendar.Source != "none")
            parts.Add(meetings.Count == 0 ? "No meetings." : $"{meetings.Count} meeting{(meetings.Count == 1 ? "" : "s")}, first {meetings[0].Subject} at {meetings[0].Start:HH:mm}.");
        if (Hub.Notes.Linked)
            parts.Add(todos == 0 ? "Nothing open on the to-do front." : $"{todos} to-do{(todos == 1 ? "" : "s")} open{(carried > 0 ? $", {carried} carried over" : "")}.");
        return MorningNudge("Morning. " + (parts.Count > 0 ? string.Join(" ", parts) : "Want the rundown?"));
#endif
    }

    static Nudge MorningNudge(string text)
    {
        var n = new Nudge { Type = "morning", Text = text };
#if LITE
        n.Buttons.Add(("Morning", () => { }));
#else
        n.Buttons.Add(("Rundown", () => Hub.Vex.OpenChat(
            "Morning rundown: what's on today, what carried over from yesterday (use your diary), and what should I start with? Keep it short.")));
#endif
        n.Buttons.Add(("✕", () => { }));
        return n;
    }

#if !LITE
    /// <summary>A question Vex came up with himself (<see cref="Curiosity"/>). "Tell you" opens the chat with it.</summary>
    public Nudge Curious(string question) =>
        Wellbeing("curious", question, doneLabel: "Tell you", snoozeMinutes: 30, onDone: () => Hub.Vex.AskInChat(question), key: $"curious|{question}");
#endif

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
        return StretchNudge($"You've been sat for {mins} minutes. Stand up, stretch. I'll guard the desk.");
    }

    /// <summary>
    /// The break reminder. Only "Taking a break" / "Had my break" (or telling him in chat) count as a break and restart
    /// the sitting timer. "Not now" snoozes it; left unanswered it goes to Pending and comes round at the usual cadence.
    /// It quietly goes if a break is confirmed some other way while it's up.
    /// </summary>
    Nudge StretchNudge(string text)
    {
        var shown = DateTime.Now;
        var n = new Nudge
        {
            Type = "stretch", Text = text, OnTimeout = () => Ignored("stretch"), PendingKey = "stretch",
            StillValid = () => Hub.Presence.SittingSince < shown,
        };
        n.Buttons.Add(("Taking a break", () => { Acted("stretch"); Hub.Presence.ConfirmBreak(lunch: false); }));
        n.Buttons.Add(("Had my break", () => { Acted("stretch"); Hub.Presence.ConfirmBreak(lunch: false); }));
        n.Buttons.Add(("Not now", () => Snooze("stretch", 10)));
        n.Buttons.Add(("✕", () => Dismissed("stretch")));
        return n;
    }

    /// <summary>Puts a nudge off for a few minutes rather than its full interval.</summary>
    void Snooze(string type, double minutes) => last[type] = DateTime.Now - Every(type, Interval(type)) + TimeSpan.FromMinutes(minutes);

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
        return WaterNudge(Store.State.Coffees >= 2
            ? $"{Store.State.Coffees} coffees and, I suspect, no water. Fix one of those."
            : "Water. The boring drink. Have some.");
    }

    Nudge WaterNudge(string text) => Wellbeing("water", text, doneLabel: "Drinking");

    Nudge? Focus(DateTime now)
    {
        var app = Hub.Activity.CurrentApp;
        if (app is "" or Activity.Away or Activity.Private) return null;
        var window = TimeSpan.FromMinutes(C.FocusCheckMinutes);
        if (now - Last("focus") < Every("focus", C.FocusCheckMinutes)) return null;
        if (now - Hub.Presence.FirstSeenToday < window) return null;
        if (Hub.Activity.MinutesIn(app, now - window) < window.TotalMinutes * 0.8) return null;
        var hours = window.TotalHours;
        return FocusNudge($"That's {(hours == 2 ? "two hours" : $"{hours:0.#} hours")} mostly in {app}. Still the plan?");
    }

    Nudge FocusNudge(string text) => Wellbeing("focus", text, doneLabel: "Yes", snoozeMinutes: 30);

    Nudge? Lunch(DateTime now)
    {
        if (Store.State.DoneToday.Contains("lunch")) return null;
        if (!TimeSpan.TryParse(C.LunchBy, out var by) || now.TimeOfDay < by) return null;
        // Never guessed from time away from the desk (that's often the lab or a meeting): it keeps coming until you
        // say you've had it, here or in the chat, or dismiss it for the day.
        return now - Last("lunch") < TimeSpan.FromMinutes(25) ? null : LunchNudge("No lunch break yet. Food is a thing. Go and have some.");
    }

    /// <summary>"Going" (Vex has lunch too) or "Had lunch" record it as today's lunch; "Not now" asks again in 25 minutes.</summary>
    Nudge LunchNudge(string text)
    {
        var n = new Nudge { Type = "lunch", Text = text, StillValid = () => !Store.State.DoneToday.Contains("lunch") };
        n.Buttons.Add(("Going", () => { Acted("lunch"); Hub.Presence.ConfirmBreak(lunch: true); Hub.Vex.StartLunch(); }));
        n.Buttons.Add(("Had lunch", () => { Acted("lunch"); Hub.Presence.ConfirmBreak(lunch: true); }));
        n.Buttons.Add(("Not now", () => last["lunch"] = DateTime.Now));
        n.Buttons.Add(("✕", () => { Dismissed("lunch"); MarkDone("lunch"); }));
        return n;
    }

#if !LITE
    Nudge? EndOfDay(DateTime now)
    {
        if (Store.State.DoneToday.Contains("eod")) return null;
        if (!TimeSpan.TryParse(C.EndOfDay, out var at) || now.TimeOfDay < at) return null;
        MarkDone("eod");
        int open = Hub.Notes.OpenChecks.Count(c => c.Date == Store.Today) + Hub.Notes.Open.Count();
        return EndOfDayNudge(open > 0 ? $"End of day-ish. {open} to-do{(open == 1 ? "" : "s")} still open. Want a summary?"
                                      : "End of day-ish. Want a summary of today?");
    }

    static Nudge EndOfDayNudge(string text)
    {
        var n = new Nudge { Type = "eod", Seconds = 60, Text = text };
        n.Buttons.Add(("Summarise", () => Hub.Vex.OpenChat(
            "Summarise my day from what you've seen: what I worked on, meetings, and what's still open. " +
            "Then suggest the first thing for tomorrow. Offer to add the summary to my journal.")));
        n.Buttons.Add(("✕", () => { }));
        return n;
    }
#endif

    static void MarkDone(string what) { Store.State.DoneToday.Add(what); Store.SaveState(); }

    // ───────────── exempt: reminders, meetings, Claude Code ─────────────

    void Reminders(DateTime now)
    {
        var due = Store.State.Reminders.Where(r => r.At <= now).ToList();
        if (due.Count == 0) return;
        // A repeating one missed by a long way (the PC was off): no point showing yesterday's, just move it on.
        foreach (var r in due.Where(r => r.Repeat.Length > 0 && now - r.At > TimeSpan.FromHours(12)).ToList())
        {
            r.At = ReminderTimes.Next(r.Repeat, r.At.TimeOfDay, now);
            due.Remove(r);
            Store.SaveState();
        }
        foreach (var r in due)
        {
            bool repeats = r.Repeat.Length > 0;
            // Kept until it's actually on screen: while something's full screen it waits for the next tick.
            if (!Offer(ReminderNudge(r.Text, repeats ? r.Id : null))) return;
            if (repeats) r.At = ReminderTimes.Next(r.Repeat, r.At.TimeOfDay, now);
            else Store.State.Reminders.Remove(r);
            Store.SaveState();
        }
    }

    /// <param name="repeatId">A repeating reminder's id: it gets a "Stop these" button that cancels the rest.</param>
    static Nudge ReminderNudge(string text, string? repeatId = null)
    {
        var n = new Nudge { Type = "reminder", Exempt = true, Seconds = 120, Text = $"Reminder: {text}", Args = { ["text"] = text } };
        n.Buttons.Add(("OK", () => { }));
        n.Buttons.Add(("10 min", () => { Store.State.Reminders.Add(new Reminder { At = DateTime.Now.AddMinutes(10), Text = text }); Store.SaveState(); }));
        if (repeatId != null)
        {
            n.Args["repeat"] = repeatId;
            n.Buttons.Add(("Stop these", () => { Store.State.Reminders.RemoveAll(r => r.Id == repeatId); Store.SaveState(); Hub.Vex.Say("No more of those.", 3, important: true); }));
        }
        return n;
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
                var n = MeetingNudge(until < 1.5 ? $"{m.Subject}{where} is starting now." : $"{m.Subject}{where} in {Math.Ceiling(until)} minutes.",
                                     m.Subject, m.Start, m.End, prep: until >= 3);
                // Only counts as warned once it's on screen; until then it's retried (and stops when the meeting ends).
                if (!Offer(n)) continue;
                meetingKeys.Add($"{id}|{w}");
                foreach (var wider in warns.Where(x => x > w)) meetingKeys.Add($"{id}|{wider}");
            }
            else if (until <= -2 && until > -10 && Activity.MicInUse() == null && !Hub.Presence.Away && !meetingKeys.Contains($"{id}|late"))
            {
                if (Offer(LateNudge(m.Subject, m.Start, m.End))) meetingKeys.Add($"{id}|late");
            }
        }
    }

    /// <summary>A meeting coming up. In Pending it replaces the earlier warning for the same meeting and goes when the meeting ends.</summary>
    static Nudge MeetingNudge(string text, string subject, DateTime start, DateTime end, bool prep)
    {
        var n = new Nudge
        {
            Type = "meeting", Exempt = true, Seconds = 90, StillValid = () => DateTime.Now < end, Text = text,
            Until = end, PendingKey = $"meeting|{start:HHmm}|{subject}",
            Args = { ["subject"] = subject, ["start"] = start.ToString("o"), ["end"] = end.ToString("o") },
        };
        n.Buttons.Add(("OK", () => { }));
#if !LITE
        if (prep) n.Buttons.Add(("Prep me", () => Hub.Vex.OpenChat(
            $"I've got \"{subject}\" at {start:HH:mm}. Anything I should know or have ready? Check your pages, my journal and to-dos. Brief.")));
#endif
        return n;
    }

    static Nudge LateNudge(string subject, DateTime start, DateTime end)
    {
        var n = new Nudge
        {
            Type = "meeting", Exempt = true, Seconds = 60, Text = $"Aren't you meant to be in {subject}?",
            Until = end, PendingKey = $"meeting|{start:HHmm}|{subject}",
            Args = { ["subject"] = subject, ["start"] = start.ToString("o"), ["end"] = end.ToString("o") },
        };
        n.Buttons.Add(("On it", () => { }));
        n.Buttons.Add(("Skipping it", () => { }));
        return n;
    }

#if !LITE
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
#endif

    // ───────────── going home ─────────────

    bool packUpChecking;
    DateTime packUpSnooze = DateTime.MinValue;

    /// <summary>
    /// PackUpMinutes before today's leave time (LeaveAt, or that day's LeaveAtByDay): "pack up", with the drive home if TomTom is set up. Exempt (like a meeting), shown
    /// once a day; if it can't be shown yet it's retried until half an hour after leaving time. Also counts as the
    /// end-of-day nudge, which would otherwise come after you've gone.
    /// </summary>
    void PackUp(DateTime now)
    {
#if !LITE
        if (Hub.Where.HomeToday) return;   // nothing to pack up at home; the clock-out nudge has this slot instead
#endif
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
            var n = PackUpNudge($"Pack-up time, you're off at {leave:HH:mm}." + (traffic != null ? " " + traffic : ""));
            if (Offer(n)) { MarkDone("packup"); if (!Store.State.DoneToday.Contains("eod")) MarkDone("eod"); }
        }
        catch (Exception ex) { Store.Log($"Pack-up: {ex.Message}"); }
        finally { packUpChecking = false; }
    }

    Nudge PackUpNudge(string text)
    {
        var n = new Nudge { Type = "packup", Exempt = true, Seconds = 90, Text = text, PendingKey = "packup" };
        n.Buttons.Add(("Packing up", () => Acted("packup")));
#if !LITE
        n.Buttons.Add(("Summarise day", () => Hub.Vex.OpenChat(
            "I'm packing up. Summarise my day from what you've seen: what I worked on, meetings, and what's still open. " +
            "Then suggest the first thing for tomorrow. Offer to add the summary to my journal.")));
#endif
        n.Buttons.Add((Commute.MapLabel, Commute.OpenMap));
        n.Buttons.Add(("10 min", () => { packUpSnooze = DateTime.Now.AddMinutes(10); Store.State.DoneToday.Remove("packup"); Store.SaveState(); }));
        return n;
    }

#if !LITE
    // ───────────── home days: clocking in and out ─────────────

    /// <summary>The last clock-in or clock-out nudge shown, so it isn't knocked over by another before it's answered.</summary>
    Nudge? lastClock;
    DateTime lastClockIn = DateTime.MinValue, clockInSnooze = DateTime.MinValue, clockOutSnooze = DateTime.MinValue;

    /// <summary>
    /// On a home day (<see cref="Whereabouts"/>), with ClockNudges on and you at the desk: clock in when you first sit
    /// down, clock out at ClockOutAt. Exempt like reminders (a missed clock-in matters more than a rate limit), but
    /// answering either never counts as a break and leaves the sitting timer alone. Office and unknown days: nothing.
    /// </summary>
    bool Clock(DateTime now)
    {
        if (!C.ClockNudges || !Hub.Where.HomeToday || Hub.Presence.Away) return false;
        return ClockIn(now) || ClockOut(now);
    }

    /// <summary>
    /// First sight of you on a home day: clock in. Unanswered (or snoozed), it comes back every ClockInRepeatMinutes,
    /// at most ClockInMaxNudges times a day, and not once it's clocking-out time. Done stops it for the day.
    /// </summary>
    bool ClockIn(DateTime now)
    {
        var s = Store.State;
        if (s.DoneToday.Contains("clockin") || s.ClockInNudges >= C.ClockInMaxNudges) return false;
        if (Hub.Presence.FirstSeenToday == DateTime.MaxValue || now < clockInSnooze) return false;
        if (Whereabouts.ClockOutTime() is { } outAt && now >= outAt) return false;
        if (now - lastClockIn < TimeSpan.FromMinutes(C.ClockInRepeatMinutes)) return false;
        if (lastClock != null && Hub.Vex.ShowingNudge(lastClock)) return false;   // still up: let it be answered, no second knock
        var n = ClockInNudge(s.ClockInNudges switch
        {
            0 => "Working from home today. Clock in.",
            1 => "Clocked in yet? The system won't do it for you.",
            _ => "Last time I'll say it: clock in.",
        });
        if (!Offer(n)) return false;   // full screen, or another exempt nudge is up: next tick
        lastClock = n; lastClockIn = now;
        s.ClockInNudges++;
        Store.SaveState();
        return true;
    }

    Nudge ClockInNudge(string text)
    {
        var n = new Nudge
        {
            Type = "clockin", Exempt = true, Seconds = 90, Text = text, PendingKey = "clockin", Until = DateTime.Today.AddDays(1),
            StillValid = () => !Store.State.DoneToday.Contains("clockin"),
        };
        n.Buttons.Add(("Done", () => { MarkDone("clockin"); Store.Log("Clock-in: done."); }));
        n.Buttons.Add(($"Snooze {C.ClockSnoozeMinutes:0} min", () => clockInSnooze = DateTime.Now.AddMinutes(C.ClockSnoozeMinutes)));
        return n;
    }

    /// <summary>
    /// ClockOutAt (else today's leave time) on a home day, in place of the pack-up nudge: clock out. Shown once (it waits until
    /// you're at the desk, any time later that day) and, like pack-up, stands in for the end-of-day nudge.
    /// </summary>
    bool ClockOut(DateTime now)
    {
        if (Store.State.DoneToday.Contains("clockout") || now < clockOutSnooze) return false;
        if (Whereabouts.ClockOutTime() is not { } at || now < at) return false;
        var n = ClockOutNudge($"Clock-out time ({at:HH:mm}). Clock out, then step away from the desk.");
        if (!Offer(n)) return false;
        lastClock = n;
        MarkDone("clockout");
        if (!Store.State.DoneToday.Contains("eod")) MarkDone("eod");
        return true;
    }

    Nudge ClockOutNudge(string text)
    {
        var n = new Nudge { Type = "clockout", Exempt = true, Seconds = 90, Text = text, PendingKey = "clockout", Until = DateTime.Today.AddDays(1) };
        n.Buttons.Add(("Done", () => Store.Log("Clock-out: done.")));
        n.Buttons.Add(($"Snooze {C.ClockSnoozeMinutes:0} min", () =>
        {
            clockOutSnooze = DateTime.Now.AddMinutes(C.ClockSnoozeMinutes);
            Store.State.DoneToday.Remove("clockout");
            Store.SaveState();
        }));
        return n;
    }

    // Goes away by itself once the session moves on (e.g. you answered Claude in VS Code). In Pending, the latest
    // nudge per project stands, and only for the hour it would have been re-nudged in.
    static Nudge ClaudeNudge(ClaudeSession s, string text, ClaudeState shownFor) =>
        ClaudeNudge(s.Project, text, () => s.State == shownFor, s.Since.AddHours(1));

    static Nudge ClaudeNudge(string project, string text, Func<bool>? stillValid = null, DateTime? until = null)
    {
        var n = new Nudge
        {
            Type = "claude", Exempt = true, Seconds = 45, Text = text, StillValid = stillValid,
            Until = until, PendingKey = $"claude|{project}", Args = { ["project"] = project },
        };
        n.Buttons.Add(("Show me", () => ClaudeWatch.FocusVsCode(project)));
        n.Buttons.Add(("✕", () => { }));
        return n;
    }

    static bool VsCodeInFront()
    {
        var h = Native.GetForegroundWindow();
        Native.GetWindowThreadProcessId(h, out uint pid);
        try { return Process.GetProcessById((int)pid).ProcessName == "Code"; } catch { return false; }
    }
#endif

    static readonly Random rng = new();
    static string Pick(params string[] s) => s[rng.Next(s.Length)];
}
