using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

namespace Vex.Services;

/// <summary>
/// Reading "when" for a reminder, without the brain: the chat's [[remind: when | text]] and anything typed by hand use it.
///   in a while:  "20m", "1h", "1h30m", "90 min"
///   a time:      "14:30", "9:15", "9am", "2.30pm"   (today, or tomorrow if it's gone)
///   a day:       "tomorrow 9:00", "fri 16:00"        (once)
///   repeating:   "daily 10:00", "weekdays 14:00", "weekends 9am", "mon,thu 9:30", "every friday 16:00", "mondays 9:00"
/// Repeats are kept as "daily", "weekdays", "weekends" or a day list like "Mon,Thu" (see <see cref="Reminder.Repeat"/>).
/// </summary>
public static class ReminderTimes
{
    static readonly string[] DayNames = { "Sun", "Mon", "Tue", "Wed", "Thu", "Fri", "Sat" };

    public static bool TryParse(string when, DateTime now, out DateTime at, out string repeat)
    {
        at = default; repeat = "";
        var s = Regex.Replace(when.Trim().ToLowerInvariant(), @"\s+", " ");

        // In a while: "20m", "1h", "1h30m", "2 hours", "90 min".
        var m = Regex.Match(s, @"^(?:in )?(?:(\d+) ?h(?:ours?|rs?)?)? ?(?:(\d+) ?m(?:in(?:ute)?s?)?)?$");
        if (m.Success && (m.Groups[1].Success || m.Groups[2].Success))
        {
            int h = m.Groups[1].Success ? int.Parse(m.Groups[1].Value) : 0, min = m.Groups[2].Success ? int.Parse(m.Groups[2].Value) : 0;
            if (h == 0 && min == 0) return false;
            at = now.AddHours(h).AddMinutes(min);
            return true;
        }

        // Otherwise: an optional day part, then a time of day at the end.
        m = Regex.Match(s, @"^(?:(.*?) ?(?:at )?)?(\d{1,2})(?:[:.](\d{2}))? ?(am|pm)?$");
        if (!m.Success || TimeOfDay(m.Groups[2].Value, m.Groups[3].Value, m.Groups[4].Value) is not { } t) return false;
        var days = m.Groups[1].Value.Trim().TrimEnd(',');

        if (days is "" or "today") { at = now.Date + t <= now ? now.Date.AddDays(1) + t : now.Date + t; return true; }
        if (days == "tomorrow") { at = now.Date.AddDays(1) + t; return true; }

        bool every = days.StartsWith("every ");
        if (every) days = days[6..].Trim();
        repeat = days switch
        {
            "day" or "daily" or "every day" => "daily",
            "weekday" or "weekdays" => "weekdays",
            "weekend" or "weekends" => "weekends",
            _ => "",
        };
        if (repeat == "")
        {
            // Day names: "mon,thu", "monday and thursday", "fridays". A plural or "every" means every week.
            var parts = Regex.Split(days, @"\s*(?:,|&|\band\b|\s)\s*").Where(p => p.Length > 0).ToList();
            var list = new List<int>();
            foreach (var p in parts)
            {
                var name = p.TrimEnd('s');
                int d = Array.FindIndex(DayNames, n => name.Length >= 3 && n.ToLowerInvariant() == name[..3] &&
                                                       (name.Length == 3 || DayLong(n).StartsWith(name)));
                if (d < 0) return false;
                if (p.EndsWith('s') && DayLong(DayNames[d]) == name) every = true;   // "fridays", but not "tues"
                if (!list.Contains(d)) list.Add(d);
            }
            if (list.Count == 0) return false;
            if (list.Count > 1) every = true;   // "mon,thu 9:30" only makes sense as every week
            var spec = string.Join(",", list.OrderBy(d => (d + 6) % 7).Select(d => DayNames[d]));
            if (!every) { at = Next(spec, t, now); return true; }   // "fri 16:00": the coming Friday, once
            repeat = spec;
        }
        at = Next(repeat, t, now);
        return true;
    }

    /// <summary>The first time after <paramref name="now"/> at this time of day on one of the repeat's days.</summary>
    public static DateTime Next(string repeat, TimeSpan t, DateTime now)
    {
        for (int i = 0; i <= 7; i++)
        {
            var at = now.Date.AddDays(i) + t;
            if (at > now && OnDay(repeat, at.DayOfWeek)) return at;
        }
        return now.Date.AddDays(1) + t;   // no days in it at all: shouldn't happen; tomorrow, then
    }

    public static bool OnDay(string repeat, DayOfWeek d) => repeat switch
    {
        "daily" => true,
        "weekdays" => d is not (DayOfWeek.Saturday or DayOfWeek.Sunday),
        "weekends" => d is DayOfWeek.Saturday or DayOfWeek.Sunday,
        _ => repeat.Split(',').Contains(DayNames[(int)d]),
    };

    /// <summary>"every weekday at 14:00", "Fri 16:00", "today 14:30": for confirmations and the brain's context.</summary>
    public static string Describe(Reminder r, DateTime now)
    {
        if (r.Repeat is { Length: > 0 } rep)
        {
            var days = rep.Split(',');   // "Mon,Wed,Fri" → "Mon, Wed and Fri"
            var list = days.Length == 1 ? days[0] : string.Join(", ", days.Take(days.Length - 1)) + " and " + days[^1];
            return $"{(rep == "daily" ? "every day" : rep == "weekdays" ? "every weekday" : rep == "weekends" ? "weekends" : "every " + list)} at {r.At:HH:mm}";
        }
        if (r.At.Date == now.Date) return $"{r.At:HH:mm}";
        if (r.At.Date == now.Date.AddDays(1)) return $"tomorrow {r.At:HH:mm}";
        return r.At < now.Date.AddDays(7) ? $"{r.At:ddd HH:mm}" : $"{r.At:ddd d MMM HH:mm}";
    }

    static TimeSpan? TimeOfDay(string hh, string mm, string ampm)
    {
        int h = int.Parse(hh), min = mm.Length > 0 ? int.Parse(mm) : 0;
        if (ampm == "" && mm.Length == 0) return null;   // a bare "9" is too vague (minutes? hours? 9 o'clock?)
        if (ampm == "pm" && h < 12) h += 12;
        if (ampm == "am" && h == 12) h = 0;
        if (h > 23 || min > 59 || (ampm != "" && int.Parse(hh) > 12)) return null;
        return new TimeSpan(h, min, 0);
    }

    static string DayLong(string shortName) =>
        CultureInfo.InvariantCulture.DateTimeFormat.DayNames[Array.IndexOf(DayNames, shortName)].ToLowerInvariant();
}
