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
/// </summary>
public class Nudges
{
    readonly Dictionary<string, DateTime> last = new();
    readonly HashSet<string> meetingKeys = new();
    DateTime lastNormal = DateTime.MinValue;

    VexConfig C => Store.Config;

    // ───────────── showing ─────────────

    public bool Offer(Nudge n)
    {
        if (!n.Exempt)
        {
            if (Hub.Quiet || Hub.Presence.Away) return false;
            if (DateTime.Now - lastNormal < TimeSpan.FromMinutes(C.NudgeGapMinutes)) return false;
            lastNormal = DateTime.Now;
        }
        else if (Hub.FullScreen) return false;
        last[n.Type] = DateTime.Now;
        Hub.Vex.ShowNudge(n);
        return true;
    }

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
        _ => 60,
    };

    // ───────────── the rules ─────────────

    public void Tick()
    {
        var now = DateTime.Now;
        Reminders(now);
        Meetings(now);
        ClaudeRenudge(now);

        var p = Hub.Presence;
        if (p.Away || p.OnBreak || Hub.Quiet) return;
        if (now - lastNormal < TimeSpan.FromMinutes(C.NudgeGapMinutes)) return;

        foreach (var make in new Func<DateTime, Nudge?>[] { Lunch, Stretch, Eyes, Water, Focus, EndOfDay })
            if (make(now) is { } n && Offer(n)) return;
    }

    DateTime SinceSitting(string type) => Last(type) > Hub.Presence.SittingSince ? Last(type) : Hub.Presence.SittingSince;

    Nudge? Stretch(DateTime now)
    {
        var since = SinceSitting("stretch");
        if (now - since < Every("stretch", C.StretchEveryMinutes)) return null;
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
            Store.State.Reminders.Remove(r);
            var n = new Nudge { Type = "reminder", Exempt = true, Seconds = 120, Text = $"Reminder: {r.Text}" };
            n.Buttons.Add(("OK", () => { }));
            n.Buttons.Add(("10 min", () => { Store.State.Reminders.Add(new Reminder { At = DateTime.Now.AddMinutes(10), Text = r.Text }); Store.SaveState(); }));
            Offer(n);
        }
        Store.SaveState();
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
                if (w == 0 || !meetingKeys.Add($"{id}|{w}")) continue;
                foreach (var wider in warns.Where(x => x > w)) meetingKeys.Add($"{id}|{wider}");
                var where = string.IsNullOrWhiteSpace(m.Location) ? "" : $" ({m.Location})";
                var n = new Nudge
                {
                    Type = "meeting", Exempt = true, Seconds = 90, StillValid = () => DateTime.Now < m.End,
                    Text = until < 1.5 ? $"{m.Subject}{where} is starting now." : $"{m.Subject}{where} in {Math.Ceiling(until)} minutes.",
                };
                n.Buttons.Add(("OK", () => { }));
                Offer(n);
            }
            else if (until <= -2 && until > -10 && Activity.MicInUse() == null && !Hub.Presence.Away && meetingKeys.Add($"{id}|late"))
            {
                var n = new Nudge { Type = "meeting", Exempt = true, Seconds = 60, Text = $"Aren't you meant to be in {m.Subject}?" };
                n.Buttons.Add(("On it", () => { }));
                n.Buttons.Add(("Skipping it", () => { }));
                Offer(n);
            }
        }
    }

    public void OnClaude(ClaudeSession s)
    {
        if (s.State == ClaudeState.NeedsYou)
        {
            s.LastNudge = DateTime.Now;
            Offer(ClaudeNudge(s, $"{s.Project}: {s.Message ?? "Claude needs you."}", ClaudeState.NeedsYou));
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
                s.LastNudge = now;
                Offer(ClaudeNudge(s, $"Still waiting on you: {s.Project}. {(int)(now - s.Since).TotalMinutes} minutes and counting.", ClaudeState.NeedsYou));
            }
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
