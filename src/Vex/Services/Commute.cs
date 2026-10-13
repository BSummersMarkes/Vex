using System;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;

namespace Vex.Services;

/// <summary>
/// Going home. PackUpMinutes before LeaveAt (on CommuteDays) he knocks: time to pack up, with the drive home as it
/// stands. Live traffic comes from TomTom's routing API when TomTomKey and both addresses are set (free key from
/// developer.tomtom.com; the addresses and route go to TomTom, nothing else). Without a key the nudge just has a
/// "Map" button that opens Google Maps directions in the browser. "[[traffic: now]]" in chat checks on demand.
/// </summary>
public static class Commute
{
    static readonly HttpClient http = new() { Timeout = TimeSpan.FromSeconds(10) };
    static VexConfig C => Store.Config;

    /// <summary>Latest check, for the chat context ("how's the traffic?" right after the nudge).</summary>
    static (DateTime At, string Text)? lastCheck;

    public static bool Enabled => TryLeave(out _);
    /// <summary>Live traffic is possible: key and both addresses set.</summary>
    public static bool CanCheck => C.TomTomKey.Length > 0 && C.HomeAddress.Length > 0 && C.WorkAddress.Length > 0;

    static bool TryLeave(out DateTime leave)
    {
        leave = default;
        if (ParseTime(C.LeaveAt) is not { } t) return false;
        leave = DateTime.Today + t;
        return true;
    }

    /// <summary>"16:45" or "9:30" as a time of day; anything else (including "17", which TimeSpan reads as days) is null.</summary>
    public static TimeSpan? ParseTime(string s) =>
        TimeSpan.TryParseExact(s.Trim(), new[] { @"h\:mm", @"hh\:mm" }, CultureInfo.InvariantCulture, out var t) && t < TimeSpan.FromDays(1) ? t : null;

    public static bool IsCommuteDay(DateTime d) =>
        C.CommuteDays.Any(x => x.Length >= 3 && d.DayOfWeek.ToString().StartsWith(x[..3], StringComparison.OrdinalIgnoreCase));

    /// <summary>After Setup…: addresses may have changed.</summary>
    public static void Reset() => lastCheck = null;

    /// <summary>One line for the chat context.</summary>
    public static string? Summary()
    {
        if (!TryLeave(out var leave) || !IsCommuteDay(DateTime.Today)) return null;
        var s = $"Leaves work at {leave:HH:mm} (pack-up nudge at {leave.AddMinutes(-C.PackUpMinutes):HH:mm}).";
        if (lastCheck is { } lc && DateTime.Now - lc.At < TimeSpan.FromMinutes(45)) s += $" Traffic at {lc.At:HH:mm}: {lc.Text}";
        return s;
    }

    /// <summary>When the pack-up nudge is due today (null if it isn't, or not set up).</summary>
    public static DateTime? PackUpAt() =>
        TryLeave(out var leave) && IsCommuteDay(DateTime.Today) ? leave.AddMinutes(-C.PackUpMinutes) : null;

    public static DateTime? LeaveTime() => TryLeave(out var leave) ? leave : null;

    /// <summary>"Home's 34 min right now, 9 slower than usual. …", or null if there's no live traffic.</summary>
    /// <param name="maxAge">Reuse the last result if it's at least this fresh (saves calls when a nudge is retried).</param>
    public static async Task<string?> Check(TimeSpan? maxAge = null)
    {
        if (!CanCheck) return null;
        if (maxAge is { } age && lastCheck is { } lc && DateTime.Now - lc.At < age) return lc.Text;
        try
        {
            var from = await Geocode(C.WorkAddress);
            var to = await Geocode(C.HomeAddress);
            if (from == null || to == null) return "I couldn't find one of the addresses. Check them in Setup.";
            var url = $"https://api.tomtom.com/routing/1/calculateRoute/{from}:{to}/json?key={Uri.EscapeDataString(C.TomTomKey)}" +
                      "&traffic=true&travelMode=car&computeTravelTimeFor=all";
            using var doc = JsonDocument.Parse(await http.GetStringAsync(url));
            var sum = doc.RootElement.GetProperty("routes")[0].GetProperty("summary");
            int Min(string n) => sum.TryGetProperty(n, out var v) ? (int)Math.Round(v.GetDouble() / 60) : -1;
            int now = Min("travelTimeInSeconds"), usual = Min("historicTrafficTravelTimeInSeconds");
            if (usual <= 0) usual = Min("noTrafficTravelTimeInSeconds");
            int slower = now - usual;
            var text = $"Home's {now} min right now" + (usual <= 0 ? "." :
                slower >= C.TrafficSlowMinutes ? $", {slower} slower than usual. Might want to get going." :
                slower >= 3 ? $", {slower} slower than usual." :
                slower <= -3 ? $", quicker than usual." : ", about normal.");
            lastCheck = (DateTime.Now, text);
            return text;
        }
        catch (Exception ex)
        {
            Store.Log($"Traffic: {ex.Message}");
            return "Couldn't get the traffic just now.";
        }
    }

    /// <summary>For the [[traffic]] chat action: checks, then says it in a bubble.</summary>
    public static async Task SayNow()
    {
        var text = await Check() ?? "I've no live traffic without a TomTom key. Here's the map.";
        Hub.Vex.Say(text, 10, important: true);   // you asked
        if (!CanCheck) OpenMap();
    }

    /// <summary>"lat,lon" for an address, looked up once and kept in state.</summary>
    static async Task<string?> Geocode(string address)
    {
        if (Store.State.GeoCache.TryGetValue(address, out var hit)) return hit;
        var url = $"https://api.tomtom.com/search/2/geocode/{Uri.EscapeDataString(address)}.json?key={Uri.EscapeDataString(C.TomTomKey)}&limit=1" +
                  (C.TrafficCountry.Length > 0 ? $"&countrySet={Uri.EscapeDataString(C.TrafficCountry)}" : "");
        using var doc = JsonDocument.Parse(await http.GetStringAsync(url));
        var results = doc.RootElement.GetProperty("results");
        if (results.GetArrayLength() == 0) return null;
        var pos = results[0].GetProperty("position");
        var ll = string.Create(CultureInfo.InvariantCulture, $"{pos.GetProperty("lat").GetDouble():0.######},{pos.GetProperty("lon").GetDouble():0.######}");
        Store.State.GeoCache[address] = ll;
        Store.SaveState();
        return ll;
    }

    /// <summary>Google Maps directions from work to home (traffic layer on) in the browser.</summary>
    public static void OpenMap()
    {
        var url = C.HomeAddress.Length == 0 ? "https://www.google.com/maps/@?api=1&map_action=map&layer=traffic"
            : $"https://www.google.com/maps/dir/?api=1&origin={Uri.EscapeDataString(C.WorkAddress)}&destination={Uri.EscapeDataString(C.HomeAddress)}&travelmode=driving";
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); } catch (Exception ex) { Store.Log($"Map: {ex.Message}"); }
    }
}
