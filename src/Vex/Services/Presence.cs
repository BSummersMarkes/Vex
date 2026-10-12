using System;
using System.Collections.Generic;
using Microsoft.Win32;

namespace Vex.Services;

/// <summary>
/// Are you at the desk, and for how long? Built from keyboard/mouse idle time and screen lock events only.
/// </summary>
public class Presence
{
    public DateTime SittingSince { get; private set; } = DateTime.Now;
    public DateTime FirstSeenToday { get; private set; } = DateTime.Now;
    public bool OnBreak { get; private set; }
    public bool Locked { get; private set; }
    /// <summary>Breaks taken today (start, end).</summary>
    public List<(DateTime Start, DateTime End)> Breaks { get; } = new();

    public event Action<TimeSpan>? BackFromBreak;

    DateTime lockedAt, breakStart;
    string day = Store.Today;

    public Presence()
    {
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

    public TimeSpan Sitting => OnBreak ? TimeSpan.Zero : DateTime.Now - SittingSince;

    public void Tick()
    {
        if (day != Store.Today) { day = Store.Today; Breaks.Clear(); FirstSeenToday = DateTime.MaxValue; }

        var idle = Native.IdleTime();
        bool breakNow = (Locked && DateTime.Now - lockedAt >= TimeSpan.FromMinutes(3))
                        || idle.TotalMinutes >= Store.Config.BreakMinutes;

        if (breakNow && !OnBreak)
        {
            OnBreak = true;
            breakStart = Locked ? lockedAt : DateTime.Now - idle;
        }
        else if (!breakNow && OnBreak)
        {
            OnBreak = false;
            SittingSince = DateTime.Now;
            Breaks.Add((breakStart, DateTime.Now));
            BackFromBreak?.Invoke(DateTime.Now - breakStart);
        }

        if (!Away && FirstSeenToday == DateTime.MaxValue) FirstSeenToday = DateTime.Now;
    }

    /// <summary>Earlier evidence of being at the desk today (e.g. the activity log after a restart).</summary>
    public void SeenSince(DateTime t)
    {
        if (t.Date == DateTime.Today && t < FirstSeenToday) FirstSeenToday = t;
    }

    public bool HadBreakSince(DateTime since, TimeSpan atLeast)
    {
        foreach (var (s, e) in Breaks)
            if (e > since && e - s >= atLeast) return true;
        return OnBreak && DateTime.Now - breakStart >= atLeast;
    }
}
