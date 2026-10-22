using System;
using System.Linq;
using System.Windows;

namespace Vex;

/// <summary>
/// A day he dresses up for: its config id, its name in Setup, the date it falls on in a given year, the outfit to
/// wear (the name of its drawing under Outfit in VexWindow.xaml), what he might say the first time each year, and
/// how many days ahead it goes on (null = DressUpLeadDays from config.json).
/// </summary>
record Occasion(string Id, string Name, Func<int, DateTime> Date, string Outfit, string[] Remarks, int? LeadDays = null);

/// <summary>
/// The one list of dress-up occasions. Vex and Setup → Theme both read it, so a new one is a row here plus a drawing
/// under Outfit in VexWindow.xaml. Where two overlap, the first listed wins.
/// </summary>
static class Occasions
{
    public static readonly Occasion[] All =
    {
        new("christmas", "Christmas", y => new DateTime(y, 12, 25), "SantaHat", new[]
        {
            "Someone's put a hat on me. It's staying until the 25th. Don't make it a thing.",
            "It's a hat. It's festive. We needn't discuss it.",
        }),
        new("easter", "Easter", EasterSunday, "BunnyEars", new[]
        {
            "The ears are seasonal. I wasn't consulted.",
            "Easter. Apparently this is what I look like now.",
        }),
        new("halloween", "Halloween", y => new DateTime(y, 10, 31), "OniMask", new[]
        {
            "It's a hannya mask. Worn on the side, so I can still see what you're up to.",
            "Halloween. The mask is for the demons. I'm keeping an eye on you myself.",
        }),
    };

    public static Occasion? Find(string id) => All.FirstOrDefault(o => string.Equals(o.Id, id, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// The occasion whose outfit is on for <paramref name="day"/>, and the year it belongs to (so a lead-in that starts
    /// in December for a January date counts towards the right year). On from its lead days before through the day itself.
    /// </summary>
    public static (Occasion Occasion, int Year)? On(DateTime day)
    {
        day = day.Date;
        foreach (var o in All)
            foreach (int year in new[] { day.Year, day.Year + 1 })
            {
                var date = o.Date(year).Date;
                int lead = Math.Max(0, o.LeadDays ?? Store.Config.DressUpLeadDays);
                if (day >= date.AddDays(-lead) && day <= date) return (o, year);
            }
        return null;
    }

    /// <summary>Easter Sunday in the Gregorian calendar (the anonymous Gregorian, or Meeus/Jones/Butcher, algorithm).</summary>
    public static DateTime EasterSunday(int year)
    {
        int a = year % 19, b = year / 100, c = year % 100, d = b / 4, e = b % 4;
        int f = (b + 8) / 25, g = (b - f + 1) / 3, h = (19 * a + b - d - g + 15) % 30;
        int i = c / 4, k = c % 4, l = (32 + 2 * e + 2 * i - h - k) % 7, m = (a + 11 * h + 22 * l) / 451;
        int n = h + l - 7 * m + 114;
        return new DateTime(year, n / 31, n % 31 + 1);
    }
}

/// <summary>
/// Seasonal dress-up: around Christmas, Easter and Halloween he wears that occasion's outfit (Occasions.All), drawn
/// inside his hood so it follows every pose. DressUp in config.json turns it off; DressUpPreview forces one on for
/// trying it out. The first time an outfit goes on each year he says one dry line about it, once you're there to see.
/// </summary>
public partial class VexWindow
{
    double nextOutfitCheck;
    string wornOutfit = "";
    /// <summary>"id-year" of the outfit still waiting for its once-a-year remark (a quiet moment), else null.</summary>
    string? outfitRemark;

    /// <summary>Setup has changed the dress-up settings (or is previewing one): look again on the next frame.</summary>
    public void RefreshOutfit() => nextOutfitCheck = 0;

    /// <summary>Called every frame; the date and settings are only looked at every 30 seconds.</summary>
    void UpdateOutfit()
    {
        if (outfitRemark != null) TryOutfitRemark();
        if (now < nextOutfitCheck) return;
        nextOutfitCheck = now + 30;

        var c = Store.Config;
        Occasion? wear = null;
        bool preview = c.DressUpPreview.Length > 0 && Occasions.Find(c.DressUpPreview) != null;
        if (preview) wear = Occasions.Find(c.DressUpPreview);
        else if (c.DressUp && Occasions.On(DateTime.Today) is { } on)
        {
            wear = on.Occasion;
            // Previews don't use up the year's remark: that's saved for the real thing.
            string key = $"{on.Occasion.Id}-{on.Year}";
            if (!Store.State.DressUpRemarked.Contains(key)) outfitRemark = key;
        }
        if (wear == null || preview) outfitRemark = null;

        string want = wear?.Outfit ?? "";
        if (want == wornOutfit) return;
        wornOutfit = want;
        foreach (var part in Outfit.Children.OfType<FrameworkElement>())
            part.Visibility = part.Name == want ? Visibility.Visible : Visibility.Collapsed;
        Store.Log(want.Length > 0 ? $"Dressed up for {wear!.Name}{(preview ? " (preview)" : "")}" : "Outfit off");
    }

    /// <summary>
    /// The once-a-year line about the outfit: only when he's just standing about in plain view, nothing else is being
    /// said or asked, and you've been active in the last minute, so it isn't missed or in the way.
    /// </summary>
    void TryOutfitRemark()
    {
        if (mode != Mode.Idle || act != Act.None || peeking || thinking || hidden || autoHidden || LunchNapping || chat != null
            || currentNudge != null || Bubble.Visibility == Visibility.Visible || Native.IdleTime().TotalMinutes >= 1) return;
        var key = outfitRemark!;
        int dash = key.LastIndexOf('-');
        var o = Occasions.Find(key.Substring(0, dash));
        int year = int.Parse(key.Substring(dash + 1));
        outfitRemark = null;
        // Keys from before last year can never come round again, so the list stays a handful long.
        var remarked = Store.State.DressUpRemarked;
        remarked.RemoveAll(k => !int.TryParse(k.Substring(k.LastIndexOf('-') + 1), out var y) || y < year - 1);
        remarked.Add(key);
        Store.SaveState();
        if (o != null) Say(Pick(o.Remarks), 6);
    }
}
