using System;
using System.Collections.Generic;
using System.Windows;

namespace Vex;

/// <summary>
/// Your birthday, as set in Setup → You (BirthdayDay / BirthdayMonth, year optional, in config.json), and when it's due
/// to be marked. Everything here stays on this PC: it isn't put in the chat context or sent anywhere.
/// </summary>
static class Birthdays
{
    static VexConfig C => Store.Config;

    /// <summary>A whole day and month are set (any day that exists in a leap year, so 29 Feb counts).</summary>
    public static bool Set => C.BirthdayMonth is >= 1 and <= 12 && C.BirthdayDay >= 1 && C.BirthdayDay <= DateTime.DaysInMonth(2024, C.BirthdayMonth);

    /// <summary>Your birthday in <paramref name="year"/>; 29 Feb falls on the 28th in other years. Null if none is set.</summary>
    public static DateTime? On(int year) =>
        Set && year is > 1 and < 9999 ? new DateTime(year, C.BirthdayMonth, Math.Min(C.BirthdayDay, DateTime.DaysInMonth(year, C.BirthdayMonth))) : null;

    /// <summary>The most recent birthday on or before <paramref name="day"/> (last year's if this year's is still to come).</summary>
    public static DateTime? Last(DateTime day)
    {
        day = day.Date;
        return On(day.Year) is { } d && d <= day ? d : On(day.Year - 1);
    }

    /// <summary>
    /// The birthday still waiting for its greeting on <paramref name="today"/>: the latest one, if this year's greeting
    /// hasn't been given and it's no more than BirthdayLateDays ago. Else null.
    /// </summary>
    public static DateTime? Due(DateTime today)
    {
        if (Last(today) is not { } d || d.Year <= Store.State.BirthdayGreetedYear) return null;
        return (today.Date - d).TotalDays <= Math.Max(0, C.BirthdayLateDays) ? d : null;
    }

    /// <summary>He wears the party hat all day on the birthday itself, and all day on the day a late greeting was given.</summary>
    public static bool HatToday(DateTime today) =>
        Set && (On(today.Year) == today.Date || Store.State.BirthdayGreetedOn == today.ToString("yyyy-MM-dd"));

    /// <summary>The greeting for <paramref name="birthday"/> has been given: not again until next year's.</summary>
    public static void Greeted(DateTime birthday)
    {
        Store.State.BirthdayGreetedYear = birthday.Year;
        Store.State.BirthdayGreetedOn = Store.Today;
        Store.SaveState();
    }

    /// <summary>
    /// Setup saved a new date. One that's already gone by (earlier this year, or late last year) isn't wished happy
    /// birthday for after the fact: he didn't know. Today still counts, so setting it on the day works.
    /// </summary>
    public static void Changed()
    {
        if (Last(DateTime.Today) is not { } d || d >= DateTime.Today) return;
        Store.State.BirthdayGreetedYear = Math.Max(Store.State.BirthdayGreetedYear, d.Year);
        Store.SaveState();
    }

    /// <summary>How old you're turning on <paramref name="birthday"/>, if the year was given.</summary>
    public static int? Age(DateTime birthday)
    {
        int age = birthday.Year - C.BirthdayYear;
        return C.BirthdayYear > 0 && age is > 0 and < 130 ? age : null;
    }
}

/// <summary>
/// Your birthday: on the day (or the first time he sees you afterwards, if you weren't about) he wishes you happy
/// birthday, once a year, fires a party popper (PartyPopper.cs) and wears a party hat for the rest of that day
/// (Occasions.Birthday, worn through the outfit code in VexOutfits.cs).
/// </summary>
public partial class VexWindow
{
    /// <summary>The birthday whose greeting is waiting for you to be about, else null. Refreshed with the outfit, every 30 s.</summary>
    DateTime? birthdayDue;
    /// <summary>Setup's "Try it now": the party hat stays on for this day too. Memory only, so a restart takes it off.</summary>
    DateTime birthdayPreviewDay;

    bool PartyHatOn => birthdayPreviewDay == DateTime.Today || Birthdays.HatToday(DateTime.Today);

    void CheckBirthday() => birthdayDue = Birthdays.Due(DateTime.Today);

    /// <summary>
    /// Waits until you're actually there to see it: active in the last minute, not in a call or full screen, and him
    /// in plain view with nothing else being said or asked. Coming back after the day counts as a late greeting.
    /// </summary>
    void TryBirthdayGreeting()
    {
        if (hidden || autoHidden || LunchNapping || chat != null || currentNudge != null || Bubble.Visibility == Visibility.Visible
            || mode is not (Mode.Idle or Mode.Walking) || Services.Hub.Quiet || Native.IdleTime().TotalMinutes >= 1) return;
        var day = birthdayDue!.Value;
        birthdayDue = null;
        if (mode == Mode.Walking) mode = Mode.Idle;   // stops to say it
        Birthdays.Greeted(day);
        Store.Log($"Birthday greeting{(day < DateTime.Today ? $", {(DateTime.Today - day).Days} day(s) late" : "")}");
        Celebrate(BirthdayLine(day));
    }

    /// <summary>Setup → You → Try it now: the greeting, popper and hat straight away. Doesn't count as this year's.</summary>
    public void BirthdayPreview()
    {
        birthdayPreviewDay = DateTime.Today;
        Celebrate(BirthdayLine(DateTime.Today));
    }

    /// <summary>The birthday's been cleared in Setup: a hat left on by Try it now comes off too.</summary>
    public void EndBirthdayPreview()
    {
        birthdayPreviewDay = default;
        RefreshOutfit();
    }

    /// <summary>Hat on at once, then the line and the party popper from his free (left) mitten, aimed away from him.</summary>
    void Celebrate(string line)
    {
        RefreshOutfit();
        Say(line, 9, important: true);
        if (hidden || autoHidden) return;
        // The left mitten sits 31 DIP left of centre and 44 up from his feet in the drawing (Facing mirrors it); the
        // popper is held up a little above it.
        PartyPopper.Fire(x - 31 * facing * S, y - 60 * S, -facing, ScreenAt(x));
    }

    /// <summary>
    /// What he says: short, dry, quietly pleased for you; never an exclamation mark. Late (you weren't about on the
    /// day), he says so in passing.
    /// </summary>
    string BirthdayLine(DateTime birthday)
    {
        string you = Store.Config.UserName.Trim() is { Length: > 0 } n ? ", " + n : "";
        int late = (int)(DateTime.Today - birthday.Date).TotalDays;
        var lines = new List<string>();
        if (late <= 0)
        {
            lines.Add($"Happy birthday{you}. I got a hat for the occasion. It's mine, but the thought's yours.");
            lines.Add($"It's your birthday. Noted, marked, confetti deployed. Happy birthday{you}.");
            lines.Add($"Happy birthday{you}. Go easy on yourself today. That's not a nudge. Mostly.");
            lines.Add("Happy birthday. There's no cake on my side of the glass, so that one's on you.");
            if (Birthdays.Age(birthday) is { } age) lines.Add($"Happy birthday{you}. {age}. I won't tell anyone.");
        }
        else
        {
            lines.Add($"Happy birthday{you}. Late, but I'd have said it on the day if you'd been here.");
            lines.Add($"You weren't about on your birthday, so I saved it. Happy birthday{you}.");
            lines.Add("Happy birthday. A bit late. The hat waited, so I did too.");
            if (late == 1) lines.Add($"Happy birthday for yesterday{you}. I kept the confetti.");
        }
        return Pick(lines.ToArray());
    }
}
