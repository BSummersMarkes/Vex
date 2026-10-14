using System;
using System.Linq;
using System.Windows.Threading;

namespace Vex.Services;

/// <summary>
/// The lite build's wiring, in place of the full Hub: the same timers and quiet rules (in a call, full screen) driving
/// presence, nudges, reminders, meetings (classic Outlook, if it's running) and going home. No brain, notes, diary,
/// memory or Claude Code: nothing here talks to anything outside the PC.
/// </summary>
public static class Hub
{
    public static VexWindow Vex { get; private set; } = null!;
    public static Presence Presence { get; } = new();
    /// <summary>Which app is in front, in memory only, for the focus check and the remote-session stretch hold.</summary>
    public static Activity Activity { get; } = new();
    public static Calendar Calendar { get; } = new();
    public static Nudges Nudges { get; } = new();
    public static string Who => Store.Config.UserName.Length > 0 ? Store.Config.UserName : "you";

    /// <summary>In a call, or something's full screen: no wellbeing nudges.</summary>
    public static bool Quiet => InCall != null || FullScreen;
    public static string? InCall { get; private set; }
    public static bool FullScreen { get; private set; }

    /// <summary>The full Vex's "question he asked" slot; the lite panel never asks, so it's always empty.</summary>
    public static string? PendingQuestion { get => null; set { } }

    static DispatcherTimer? fast, slow;

    public static void Start(VexWindow vex)
    {
        Vex = vex;
        Store.Config.ActivityLog = false;   // never written to disk in the lite build
        Store.SaveConfig();
        Calendar.Refresh();
        Presence.BackAfterAway += WelcomeBack;

        fast = new DispatcherTimer(TimeSpan.FromSeconds(5), DispatcherPriority.Background, (_, _) => Fast(), Vex.Dispatcher);
        slow = new DispatcherTimer(TimeSpan.FromSeconds(15), DispatcherPriority.Background, (_, _) => Slow(), Vex.Dispatcher);
        fast.Start(); slow.Start();

        // Testing aid, like VEX_DATA_DIR: VEX_OPEN_PANEL=1 opens the panel a moment after starting.
        if (Environment.GetEnvironmentVariable("VEX_OPEN_PANEL") == "1")
        {
            var once = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
            once.Tick += (_, _) => { once.Stop(); Vex.OpenChat(); };
            once.Start();
        }
    }

    /// <summary>After Setup…: the going-home details may have changed.</summary>
    public static void ApplySettings() => Commute.Reset();

    static void Fast()
    {
        Store.RollDay();
        Presence.Tick();
        Activity.Tick(Presence.Away);
        FullScreen = Activity.FullScreen();
    }

    static void Slow()
    {
        InCall = Activity.MicInUse();
        Calendar.Tick();
        Nudges.Tick();
        Pending.Prune();
    }

    public static void Stop() => Activity.Flush();

    static void WelcomeBack(TimeSpan away)
    {
        if (away < TimeSpan.FromMinutes(15) || Quiet || Vex.LunchRecent) return;   // his lunch wake-up line says it already
        var next = Calendar.Upcoming.FirstOrDefault(m => m.Start > DateTime.Now && m.Start - DateTime.Now < TimeSpan.FromMinutes(45));
        var reminder = Store.State.Reminders.Where(r => r.At > DateTime.Now && r.At - DateTime.Now < TimeSpan.FromMinutes(45)).OrderBy(r => r.At).FirstOrDefault();
        var line = next != null ? $"Welcome back. {next.Subject} at {next.Start:HH:mm}."
                 : reminder != null ? $"Welcome back. Reminder at {reminder.At:HH:mm}: {reminder.Text}."
                 : "Oh. You're back. Nothing happened. I checked.";
        Vex.Say(line, 6);
    }
}
