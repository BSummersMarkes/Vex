using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace Vex.Services;

public record Meeting(string Subject, DateTime Start, DateTime End, string Location, bool AllDay);

/// <summary>
/// Today's meetings. Classic Outlook (read locally over COM, only if it's already running), otherwise a
/// calendar.json written by a Power Automate flow into OneDrive. The new Outlook has no local API.
/// </summary>
public class Calendar
{
    public List<Meeting> Today { get; private set; } = new();
    public string Source { get; private set; } = "none";
    DateTime lastRefresh = DateTime.MinValue;

    public string JsonPath => Store.Config.CalendarJsonPath ?? Path.Combine(
        Environment.GetEnvironmentVariable("OneDriveCommercial") ?? Environment.GetEnvironmentVariable("OneDrive") ?? "",
        "Vex", "calendar.json");

    public void Tick()
    {
        if (DateTime.Now - lastRefresh < TimeSpan.FromMinutes(5) && lastRefresh.Date == DateTime.Today) return;
        Refresh();
    }

    public void Refresh()
    {
        lastRefresh = DateTime.Now;
        if (Process.GetProcessesByName("OUTLOOK").Length > 0 && FromOutlook() is { } o) { Today = o; Source = "Outlook"; return; }
        if (FromJson() is { } j) { Today = j; Source = "calendar.json"; return; }
        Source = "none";
    }

    public IEnumerable<Meeting> Upcoming => Today.Where(m => !m.AllDay && m.End > DateTime.Now).OrderBy(m => m.Start);

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
            var from = DateTime.Today; var to = from.AddDays(1);
            string f(DateTime d) => d.ToString("g", CultureInfo.CurrentCulture);
            dynamic found = items.Restrict($"[Start] < '{f(to)}' AND [End] > '{f(from)}'");
            var list = new List<Meeting>();
            foreach (dynamic it in found)
            {
                list.Add(new Meeting((string)(it.Subject ?? ""), (DateTime)it.Start, (DateTime)it.End,
                                     (string)(it.Location ?? ""), (bool)it.AllDayEvent));
                if (list.Count >= 60) break;
            }
            return list;
        }
        catch (Exception ex) { Store.Log($"Outlook calendar: {ex.Message}"); return null; }
    }

#if LITE
    List<Meeting>? FromJson() => null;   // lite: classic Outlook only, no Power Automate export
#else
    /// <summary>
    /// Accepts the Power Automate "Get calendar view of events (V3)" output (or its "value" array).
    /// Times with an offset are honoured; bare times are treated as UTC, which is what that action returns.
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
            foreach (var e in arr.EnumerateArray())
            {
                var start = Time(e, "startWithTimeZone", "start", "Start");
                var end = Time(e, "endWithTimeZone", "end", "End");
                if (start == null || end == null || end.Value.Date < DateTime.Today || start.Value.Date > DateTime.Today) continue;
                list.Add(new Meeting(Str(e, "subject", "Subject") ?? "(no subject)", start.Value, end.Value,
                                     Str(e, "location", "Location") ?? "",
                                     e.TryGetProperty("isAllDay", out var ad) && ad.ValueKind == JsonValueKind.True));
            }
            return list.OrderBy(m => m.Start).ToList();
        }
        catch (Exception ex) { Store.Log($"calendar.json: {ex.Message}"); return null; }
    }

    static string? Str(JsonElement e, params string[] names)
    {
        foreach (var n in names)
            if (e.TryGetProperty(n, out var v))
            {
                if (v.ValueKind == JsonValueKind.String) return v.GetString();
                if (v.ValueKind == JsonValueKind.Object && v.TryGetProperty("displayName", out var d)) return d.GetString();
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
