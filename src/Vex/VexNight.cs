using System;
using System.Globalization;
using System.Windows;

namespace Vex;

/// <summary>
/// Good night. When you say good night (or goodbye for the day) in chat, [[sleep: night]] puts him to sleep until the
/// next morning: once the chat's closed and he's free he dozes off as for a nap, but keyboard and mouse don't wake
/// him. He wakes on your first input after NightWakeTime (07:00) the next morning, with a short line, not before; if
/// you're not back by then (a weekend) he simply sleeps on. Things that would wake him from a nap still do for a
/// moment (opening the chat, an important nudge, being picked up, a window covering him), and once that's over he
/// goes back to sleep. Wellbeing nudges and casual remarks are held back all night, as during his lunch nap.
/// [[sleep: wake]] in chat, or Setup → Wake up, ends it early. Kept in state.json so a restart overnight (Windows
/// updates) doesn't wake him.
/// </summary>
public partial class VexWindow
{
    double nightOverAt = -99;   // when he last woke for the morning, so "welcome back" doesn't talk over his good-morning line

    /// <summary>Asleep for the night (or due to be once he's free): wellbeing nudges and casual bubbles wait.</summary>
    public bool NightSleeping => Store.State.NightUntil is not null;

    /// <summary>Asleep for the night, or only just up (his good-morning line already covers "welcome back").</summary>
    public bool NightRecent => NightSleeping || now - nightOverAt < 90;

    /// <summary>A line for the chat's context while he's asleep for the night, so the brain knows. Null otherwise.</summary>
    public string? NightStatus() => Store.State.NightUntil is { } until
        ? $"You're asleep for the night ({Services.Hub.Who} said good night); you wake when they're back after {until:ddd HH:mm}. " +
          "They've opened the chat anyway, so you're awake for it and go back to sleep when it closes. [[sleep: wake]] if they want you up."
        : null;

    /// <summary>
    /// [[sleep: night]]: sleep until the next NightWakeTime. He drops off in UpdateNight once the chat is closed and
    /// nothing else is going on, so the reply can still be read. Returns the morning it runs to.
    /// </summary>
    public DateTime StartNight()
    {
        var until = DateTime.Today + WakeTime();
        if (until <= DateTime.Now) until = until.AddDays(1);   // said in the evening: tomorrow; in the small hours: this morning
        Store.State.NightUntil = until;
        Store.SaveState();
        Store.Log($"Night: asleep until you're back after {until:yyyy-MM-dd HH:mm}");
        return until;
    }

    /// <summary>[[sleep: wake]], or Setup → Wake up: the night's over early. Wakes him if he's asleep. False if there was no night.</summary>
    public bool EndNight()
    {
        if (!NightSleeping) return false;
        Store.State.NightUntil = null;
        Store.SaveState();
        Store.Log("Night: ended early");
        if (mode == Mode.Sleeping) Wake(quiet: true);
        nightOverAt = now;
        return true;
    }

    /// <summary>NightWakeTime from config ("07:00"); anything unreadable is 07:00.</summary>
    static TimeSpan WakeTime() =>
        TimeSpan.TryParse(Store.Config.NightWakeTime, CultureInfo.InvariantCulture, out var t) && t >= TimeSpan.Zero && t < TimeSpan.FromDays(1)
            ? t : TimeSpan.FromHours(7);

    /// <summary>
    /// Every frame (from UpdateActs): puts him (back) to sleep whenever he's free during the night, and wakes him for the
    /// morning on your first input after the wake time.
    /// </summary>
    void UpdateNight()
    {
        if (Store.State.NightUntil is not { } until) return;

        // Morning, and you're here (input in the last moment, not at the lock screen): up he gets.
        if (DateTime.Now >= until && Native.IdleTime().TotalSeconds < 2 && !Services.Hub.Presence.Locked)
        {
            Store.State.NightUntil = null;
            Store.SaveState();
            Store.Log("Night: morning, you're back");
            nightOverAt = now;
            if (mode == Mode.Sleeping) Wake(quiet: true);
            Say(Pick("Morning. I was up ages ago.", "Oh. Morning already.", "Morning. I slept. Did you?", "Back again. Good. It was very quiet."), 5);
            return;
        }

        // Dozed off by himself before the night started: make it a night's sleep, so moving the mouse doesn't wake him.
        if (mode == Mode.Sleeping) { napping = true; return; }

        // Free again (chat closed, nudge answered, put down, out from under a window): back to sleep. Lunch and the
        // laptop finish first; a build that runs on overnight keeps him up until it's done.
        if (mode == Mode.Idle && act == Act.None && pendingAct == Act.None && !peeking && !thinking && chat == null
            && currentNudge == null && Bubble.Visibility != Visibility.Visible && !AtLunch && !Coding)
            Sleep(requested: true);
    }
}
