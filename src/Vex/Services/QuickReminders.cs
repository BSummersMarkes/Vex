using System;
using System.Text.RegularExpressions;

namespace Vex.Services;

/// <summary>
/// Spots plain timer and reminder requests typed in the chat, so they're set at once, offline, without a round trip
/// to Claude. Deliberately narrow: only a whole message that is clearly one request, with a time
/// <see cref="ReminderTimes"/> can read, is taken; anything else ("remind me what we said about X") goes to Claude as before.
///   reminders: "remind me in 20m to call Sam", "remind me to call Sam at 3pm", "remind me tomorrow 9:00 to book the van",
///              "remind me every friday 16:00 to do timesheets"
///   timers:    "timer 10m", "set a timer for 25 minutes", "10 min timer", "timer 1h for the oven"
/// </summary>
public static class QuickReminders
{
    // "Vex, can you ...", "please ..." in front and "please" / punctuation at the end don't change what's asked.
    const string Lead = @"^(?:(?:hey |ok )?vex[,:]? )?(?:(?:can|could|would|will) you |please )?";
    const string Tail = @"(?: please)?[.!?]*$";

    // A length of time, for timers: "10m", "25 minutes", "1h30m", "1 hour 30 min".
    const string Span = @"\d+ ?(?:h|hrs?|hours?|m|mins?|minutes?)(?: ?\d+ ?(?:m|mins?|minutes?))?";

    static readonly Regex Timer = new(Lead + @"(?:set |start )?(?:a |an )?(?:timer (?:for )?(?<d>" + Span + @")|(?<d>" + Span + @") timer)(?: (?:for|to|called) (?<what>.+?))?" + Tail,
        RegexOptions.IgnoreCase);
    static readonly Regex Remind = new(Lead + @"remind me (?<rest>.+?)" + Tail, RegexOptions.IgnoreCase);

    /// <summary>
    /// Reads a typed message as a timer or reminder. <paramref name="when"/> is in <see cref="ReminderTimes"/>' terms
    /// and <paramref name="what"/> is the reminder's text. False means it isn't one (or isn't clear), so ask Claude.
    /// </summary>
    public static bool TryRead(string message, DateTime now, out string when, out string what)
    {
        when = what = "";
        var s = Regex.Replace(message.Trim(), @"\s+", " ");
        if (s.Length == 0 || s.Length > 300) return false;

        var m = Timer.Match(s);
        if (m.Success && Reads(m.Groups["d"].Value, now, out _))
        {
            when = m.Groups["d"].Value;
            var label = $"{when} timer";
            what = m.Groups["what"].Success ? $"{m.Groups["what"].Value} ({label})" : $"{label}'s up";
            return true;
        }

        m = Remind.Match(s);
        if (!m.Success) return false;
        var rest = m.Groups["rest"].Value;

        // "remind me <when> to <what>": the first " to " after a readable time.
        foreach (Match to in Regex.Matches(rest, @" to ", RegexOptions.IgnoreCase))
        {
            var w = rest[..to.Index];
            var t = rest[(to.Index + 4)..].Trim();
            if (t.Length > 0 && Reads(w, now, out var clean)) { when = clean; what = t; return true; }
        }

        // "remind me to <what> <when>": the longest readable time at the end. ReminderTimes is strict, so words from
        // the text ("call Sam at 3pm") never read as a time and the split lands on "at 3pm".
        if (!rest.StartsWith("to ", StringComparison.OrdinalIgnoreCase)) return false;
        var body = rest[3..];
        var words = body.Split(' ');
        for (int i = 1; i < words.Length; i++)
        {
            var w = string.Join(' ', words[i..]);
            if (!Reads(w, now, out var clean)) continue;
            when = clean;
            what = string.Join(' ', words[..i]);
            return true;
        }
        return false;
    }

    /// <summary>Whether ReminderTimes can read this time, allowing the "at" / "on" people put in front of it.</summary>
    static bool Reads(string when, DateTime now, out string clean)
    {
        // Not "for": in "book a table for 8pm" the time belongs to the task, so that one is left to Claude.
        clean = Regex.Replace(when.Trim(), @"^(?:at|on) ", "", RegexOptions.IgnoreCase);
        return clean.Length > 0 && ReminderTimes.TryParse(clean, now, out _, out _);
    }
}
