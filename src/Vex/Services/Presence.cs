using System;
using System.Collections.Generic;
using Microsoft.Win32;

namespace Vex.Services;

/// <summary>
/// Are you at the desk, and for how long? Built from keyboard/mouse idle time and screen lock events only.
/// Time away from the desk is never taken as a break (you're often working elsewhere: the lab, another machine, a
/// meeting); only a break or lunch you confirm, on a nudge or in the chat, restarts the sitting timer.
/// </summary>
public class Presence
{
    /// <summary>Start of the sitting timer: the last confirmed break, or when you were first seen today.</summary>
    public DateTime SittingSince { get; private set; } = DateTime.Now;
    public DateTime FirstSeenToday { get; private set; } = DateTime.Now;
    /// <summary>Away from the desk for AwayMinutes or more (or locked 3+ min). Just information, not a break.</summary>
    public bool AwayLong { get; private set; }
    /// <summary>When the current long spell away began (only meaningful while <see cref="AwayLong"/>).</summary>
    public DateTime AwaySince { get; private set; }
    public bool Locked { get; private set; }
    /// <summary>Long spells away from the desk today (start, end). Not breaks: shown to Vex as plain information.</summary>
    public List<(DateTime Start, DateTime End)> AwayTimes { get; } = new();

    /// <summary>Back at the desk after a long spell away, with how long it was.</summary>
    public event Action<TimeSpan>? BackAfterAway;

    DateTime lockedAt;
    string day = Store.Today;

    public Presence()
    {
        // A break confirmed earlier today survives a restart; restarting Vex isn't a break either (see SeenSince).
        if (LastBreak is { } b) SittingSince = b.At;
        SystemEvents.SessionSwitch += (_, e) =>
        {
            if (e.Reason is SessionSwitchReason.SessionLock or SessionSwitchReason.ConsoleDisconnect or SessionSwitchReason.RemoteDisconnect)
            { Locked = true; lockedAt = DateTime.Now; }
            else if (e.Reason is SessionSwitchReason.SessionUnlock or SessionSwitchReason.ConsoleConnect or SessionSwitchReason.RemoteConnect)
                Locked = false;
        };
    }

    /// <summary>Not touching the keyboard or mouse for a couple of minutes, or locked.</summary>
    public bool Away => Locked || Native.IdleTime().TotalMinutes >= 2;

    /// <summary>Time since the last confirmed break (or first sight of you today). Keeps running while you're away.</summary>
    public TimeSpan Sitting => DateTime.Now - SittingSince;

    /// <summary>Today's last confirmed break (kept in state.json), or null if you haven't said you've had one.</summary>
    public (DateTime At, bool Lunch)? LastBreak =>
        Store.State.LastBreakAt.Date == DateTime.Today ? (Store.State.LastBreakAt, Store.State.LastBreakKind == "lunch") : null;

    /// <summary>
    /// You've said you're taking (or have had) a break or lunch, from a nudge button or the chat's [[break]] action.
    /// The only thing that restarts the sitting timer. Lunch also counts as today's lunch, so its nudge stops.
    /// </summary>
    /// <param name="at">When it was, if you said ("had lunch at 12:30"); default now.</param>
    public void ConfirmBreak(bool lunch, DateTime? at = null)
    {
        var when = at ?? DateTime.Now;
        if (when > SittingSince) SittingSince = when;   // an older break than the timer's start doesn't wind it back
        if (LastBreak is not { } b || when >= b.At)
        {
            Store.State.LastBreakAt = when;
            Store.State.LastBreakKind = lunch ? "lunch" : "break";
        }
        if (lunch && !Store.State.DoneToday.Contains("lunch")) Store.State.DoneToday.Add("lunch");
        Store.SaveState();
    }

    public void Tick()
    {
        if (day != Store.Today) { day = Store.Today; AwayTimes.Clear(); FirstSeenToday = DateTime.MaxValue; }

        var idle = Native.IdleTime();
        bool awayNow = (Locked && DateTime.Now - lockedAt >= TimeSpan.FromMinutes(3))
                       || idle.TotalMinutes >= Store.Config.AwayMinutes;

        if (awayNow && !AwayLong)
        {
            AwayLong = true;
            AwaySince = Locked ? lockedAt : DateTime.Now - idle;
        }
        else if (!awayNow && AwayLong)
        {
            AwayLong = false;
            AwayTimes.Add((AwaySince, DateTime.Now));
            BackAfterAway?.Invoke(DateTime.Now - AwaySince);
        }

        if (!Away && FirstSeenToday == DateTime.MaxValue)
        {
            FirstSeenToday = DateTime.Now;
            // A new day: you weren't sat here all night, so the timer starts from this morning.
            if (SittingSince.Date < DateTime.Today) SittingSince = DateTime.Now;
        }
    }

    /// <summary>Earlier evidence of being at the desk today (e.g. the activity log after a restart).</summary>
    public void SeenSince(DateTime t)
    {
        if (t.Date != DateTime.Today) return;
        if (t < FirstSeenToday) FirstSeenToday = t;
        // No break confirmed today: you've been on the go since you were first seen, whatever Vex's restarts say.
        if (LastBreak == null && t < SittingSince) SittingSince = t;
    }
}
