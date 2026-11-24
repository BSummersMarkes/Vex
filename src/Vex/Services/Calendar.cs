using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Vex.Services;

/// <summary>
/// One calendar entry. The details beyond the time and place (who organised it, who's going, whether it's online, a
/// short bit of the invite's text) are filled in when <see cref="VexConfig.CalendarDetails"/> is on and the source has them.
/// </summary>
public record Meeting(string Subject, DateTime Start, DateTime End, string Location, bool AllDay)
{
    public string Organizer { get; init; } = "";
    /// <summary>Names (or addresses, from calendar.json) of the people invited, organiser excluded.</summary>
    public List<string> Attendees { get; init; } = new();
    /// <summary>A Teams (or other online) meeting.</summary>
    public bool Online { get; init; }
    /// <summary>Your answer to the invite: "declined", "tentative" or "" (accepted, yours, or not known).</summary>
    public string Response { get; init; } = "";
    /// <summary>The start of the invite's own text, joining boilerplate removed, cut to a few hundred characters.</summary>
    public string Notes { get; init; } = "";
}

/// <summary>
/// Meetings from today to the end of the next working day (or further: <see cref="VexConfig.CalendarDaysAhead"/>).
/// Classic Outlook (read locally over COM, only if it's already running), otherwise a calendar.json written by a
/// Power Automate flow into OneDrive. The new Outlook has no local API.
/// </summary>
public class Calendar
{
    /// <summary>Everything in the window, oldest first.</summary>
    public List<Meeting> All { get; private set; } = new();
    /// <summary>Meetings that touch today. Nudges, warnings and the morning brief only ever look at these.</summary>
    public List<Meeting> Today => All.Where(m => m.Start.Date <= DateTime.Today && m.End > DateTime.Today).ToList();
    /// <summary>Meetings after today, up to <see cref="Until"/>, for the chat's "what's on tomorrow?".</summary>
    public List<Meeting> Ahead => All.Where(m => m.Start >= DateTime.Today.AddDays(1)).ToList();
    public string Source { get; private set; } = "none";
    DateTime lastRefresh = DateTime.MinValue;

    public string JsonPath => Store.Config.CalendarJsonPath ?? Path.Combine(
        Environment.GetEnvironmentVariable("OneDriveCommercial") ?? Environment.GetEnvironmentVariable("OneDrive") ?? "",
        "Vex", "calendar.json");

    /// <summary>
    /// The end of the lookahead: midnight after the CalendarDaysAhead-th working day (Mon–Fri) from today, so on a
    /// Friday "the next day" means Monday. Weekend meetings in between are included. 0 = today only.
    /// </summary>
    public static DateTime Until
    {
        get
        {
            var d = DateTime.Today;
            for (int left = Math.Max(0, Store.Config.CalendarDaysAhead); left > 0;)
            {
                d = d.AddDays(1);
                if (d.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday)) left--;
            }
            return d.AddDays(1);
        }
    }

    public void Tick()
    {
        if (DateTime.Now - lastRefresh < TimeSpan.FromMinutes(5) && lastRefresh.Date == DateTime.Today) return;
        Refresh();
    }

    public void Refresh()
    {
        lastRefresh = DateTime.Now;
        if (Process.GetProcessesByName("OUTLOOK").Length > 0 && FromOutlook() is { } o) { All = o; Source = "Outlook"; return; }
        if (FromJson() is { } j) { All = j; Source = "calendar.json"; return; }
        Source = "none";
    }

    /// <summary>Today's timed meetings still to finish, declined ones left out (no warning for a meeting you're not going to).</summary>
    public IEnumerable<Meeting> Upcoming => Today.Where(m => !m.AllDay && m.End > DateTime.Now && m.Response != "declined").OrderBy(m => m.Start);

    static List<Meeting>? FromOutlook()
    {
        try
        {
            var type = Type.GetTypeFromProgID("Outlook.Application");
            if (type == null) return null;
            dynamic app = Activator.CreateInstance(type)!;
            dynamic folder = app.GetNamespace("MAPI").GetDefaultFolder(9); // olFolderCalendar
            dynamic items = folder.Items;
            items.IncludeRecurrences = true;
            items.Sort("[Start]");
            var from = DateTime.Today; var to = Until;
            string f(DateTime d) => d.ToString("g", CultureInfo.CurrentCulture);
            dynamic found = items.Restrict($"[Start] < '{f(to)}' AND [End] > '{f(from)}'");
            bool details = Store.Config.CalendarDetails;
            var list = new List<Meeting>();
            foreach (dynamic it in found)
            {
                var m = new Meeting((string)(it.Subject ?? ""), (DateTime)it.Start, (DateTime)it.End,
                                    (string)(it.Location ?? ""), (bool)it.AllDayEvent);
                if (details) m = WithOutlookDetails(m, it);
                list.Add(m);
                if (list.Count >= 80) break;
            }
            return list;
        }
        catch (Exception ex) { Store.Log($"Outlook calendar: {ex.Message}"); return null; }
    }

    /// <summary>
    /// Organiser, attendees, your response and the invite text. Each read on its own, so one Outlook won't give
    /// (some are guarded by its security prompt on PCs without up-to-date antivirus) doesn't lose the rest.
    /// </summary>
    static Meeting WithOutlookDetails(Meeting m, dynamic it)
    {
        string Get(Func<object?> read) { try { return read()?.ToString() ?? ""; } catch { return ""; } }
        var organizer = Get(() => it.Organizer);
        var body = Get(() => it.Body);
        int.TryParse(Get(() => it.ResponseStatus), out var response);   // OlResponseStatus: 2 tentative, 4 declined
        var people = (Get(() => it.RequiredAttendees) + ";" + Get(() => it.OptionalAttendees))
            .Split(';').Select(s => s.Trim()).Where(s => s.Length > 0 && !s.Equals(organizer, StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        return m with
        {
            Organizer = organizer, Attendees = people, Notes = Clean(body),
            Online = IsOnline(m.Location + " " + body),
            Response = response switch { 4 => "declined", 2 => "tentative", _ => "" },
        };
    }

    static bool IsOnline(string text) =>
        Regex.IsMatch(text, @"teams\.microsoft\.com|Microsoft Teams|zoom\.us|meet\.google\.com|webex\.com", RegexOptions.IgnoreCase);

    /// <summary>
    /// The invite's own words: HTML tags gone, cut off where the Teams/Zoom joining block starts, whitespace squashed
    /// and cut to 300 characters. It's other people's text, so the brain sees it as data like the rest of the context.
    /// </summary>
    internal static string Clean(string body)
    {
        if (string.IsNullOrWhiteSpace(body)) return "";
        var s = Regex.Replace(body, @"<(style|script)[^>]*>.*?</\1>", " ", RegexOptions.Singleline | RegexOptions.IgnoreCase);
        s = Regex.Replace(s, "<[^>]+>", " ");
        s = System.Net.WebUtility.HtmlDecode(s);
        var cut = Regex.Match(s, @"_{6,}|Microsoft Teams (meeting|Need help)|Join (the meeting now|on your computer|Zoom Meeting)|Meeting ID:", RegexOptions.IgnoreCase);
        if (cut.Success) s = s.Substring(0, cut.Index);
        s = Regex.Replace(s, @"\s+", " ").Trim();
        return s.Length <= 300 ? s : s.Substring(0, 300).TrimEnd() + "…";
    }

#if LITE
    List<Meeting>? FromJson() => null;   // lite: classic Outlook only, no Power Automate export
#else
    /// <summary>
    /// Accepts the Power Automate "Get calendar view of events (V3)" output (or its "value" array).
    /// Times with an offset are honoured; bare times are treated as UTC, which is what that action returns.
    /// For the lookahead the flow has to cover the next working day too (e.g. a calendar view to 3 days ahead).
    /// </summary>
    List<Meeting>? FromJson()
    {
        try
        {
            if (!File.Exists(JsonPath)) return null;
            using var doc = JsonDocument.Parse(File.ReadAllText(JsonPath));
            var arr = doc.RootElement.ValueKind == JsonValueKind.Array ? doc.RootElement
                    : doc.RootElement.TryGetProperty("value", out var v) ? v : default;
            if (arr.ValueKind != JsonValueKind.Array) return null;
            var list = new List<Meeting>();
            var until = Until;
            bool details = Store.Config.CalendarDetails;
            foreach (var e in arr.EnumerateArray())
            {
                var start = Time(e, "startWithTimeZone", "start", "Start");
                var end = Time(e, "endWithTimeZone", "end", "End");
                if (start == null || end == null || end.Value <= DateTime.Today || start.Value >= until) continue;
                var location = Str(e, "location", "Location") ?? "";
                var m = new Meeting(Str(e, "subject", "Subject") ?? "(no subject)", start.Value, end.Value, location,
                                    e.TryGetProperty("isAllDay", out var ad) && ad.ValueKind == JsonValueKind.True);
                if (details)
                {
                    var organizer = Str(e, "organizer", "Organizer") ?? "";
                    var body = Str(e, "body", "bodyPreview", "Body") ?? "";
                    var response = (Str(e, "responseType", "responseStatus") ?? "").ToLowerInvariant();
                    m = m with
                    {
                        Organizer = organizer,
                        Attendees = ((Str(e, "requiredAttendees") ?? "") + ";" + (Str(e, "optionalAttendees") ?? ""))
                            .Split(';').Select(s => s.Trim()).Where(s => s.Length > 0 && !s.Equals(organizer, StringComparison.OrdinalIgnoreCase))
                            .Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
                        Notes = Clean(body),
                        Online = (e.TryGetProperty("isOnlineMeeting", out var om) && om.ValueKind == JsonValueKind.True) || IsOnline(location + " " + body),
                        Response = response.Contains("declin") ? "declined" : response.Contains("tentative") ? "tentative" : "",
                    };
                }
                list.Add(m);
            }
            return list.OrderBy(m => m.Start).ToList();
        }
        catch (Exception ex) { Store.Log($"calendar.json: {ex.Message}"); return null; }
    }

    /// <summary>A string field, or the displayName/response/emailAddress inside an object one (Graph-style shapes).</summary>
    static string? Str(JsonElement e, params string[] names)
    {
        foreach (var n in names)
            if (e.TryGetProperty(n, out var v))
            {
                if (v.ValueKind == JsonValueKind.String) return v.GetString();
                if (v.ValueKind != JsonValueKind.Object) continue;
                if (v.TryGetProperty("displayName", out var d) && d.ValueKind == JsonValueKind.String) return d.GetString();
                if (v.TryGetProperty("response", out var r) && r.ValueKind == JsonValueKind.String) return r.GetString();
                if (v.TryGetProperty("content", out var c) && c.ValueKind == JsonValueKind.String) return c.GetString();
                if (v.TryGetProperty("emailAddress", out var a) && a.ValueKind == JsonValueKind.Object
                    && a.TryGetProperty("name", out var an) && an.ValueKind == JsonValueKind.String) return an.GetString();
            }
        return null;
    }

    static DateTime? Time(JsonElement e, params string[] names)
    {
        foreach (var n in names)
        {
            if (!e.TryGetProperty(n, out var v)) continue;
            string? s = v.ValueKind == JsonValueKind.String ? v.GetString()
                      : v.ValueKind == JsonValueKind.Object && v.TryGetProperty("dateTime", out var dt) ? dt.GetString() : null;
            if (s == null) continue;
            if (DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var dto))
                return dto.LocalDateTime;
        }
        return null;
    }
#endif
}
