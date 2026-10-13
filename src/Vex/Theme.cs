using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using Microsoft.Win32;

namespace Vex;

/// <summary>The colours of the chat panel and speech bubble, for one theme.</summary>
public sealed record Palette(
    Color Panel, Color PanelBorder, Color Text, Color SubText, Color Input, Color InputBorder,
    Color MeBubble, Color VexBubble, Color Note, Color Divider, Color Speech, Color Button, Color ButtonText);

/// <summary>
/// Appearance: dark or light chat and bubbles (or follow Windows), and the hoodie colour.
/// Both are set from the tray (Appearance) and kept in config.json as Theme and HoodieColour.
/// </summary>
public static class Theme
{
    public static readonly Palette Dark = new(
        Panel: C("#F2151826"), PanelBorder: C("#4A5A86"), Text: C("#E8EAF2"), SubText: C("#8790AD"),
        Input: C("#232842"), InputBorder: C("#3A4568"), MeBubble: C("#34426A"), VexBubble: C("#232842"),
        Note: C("#8CC49A"), Divider: C("#6F7792"), Speech: C("#F01C2030"), Button: C("#2E334A"), ButtonText: Colors.White);

    public static readonly Palette Light = new(
        Panel: C("#F7F8FAFD"), PanelBorder: C("#B9C2DA"), Text: C("#1E2333"), SubText: C("#687089"),
        Input: C("#FFFFFF"), InputBorder: C("#C5CCE0"), MeBubble: C("#D7E0F7"), VexBubble: C("#ECEEF4"),
        Note: C("#2F7A45"), Divider: C("#8A91A6"), Speech: C("#F7FAFBFE"), Button: C("#E1E6F2"), ButtonText: C("#1E2333"));

    /// <summary>Hoodie choices in the tray menu; anything else comes from "Custom…".</summary>
    public static readonly (string Name, string Hex)[] Hoodies =
    {
        ("Dark red (original)", "#82292F"), ("Navy", "#2B3F6B"), ("Forest green", "#2F5A3A"), ("Teal", "#1F5F63"),
        ("Purple", "#55306E"), ("Mustard", "#B08A2A"), ("Charcoal", "#3A3A40"), ("Pink", "#C26A8A"),
    };

    /// <summary>The colour the hoodie is drawn in (its mid tone); every other shade is worked out from this.</summary>
    static readonly Color Original = C("#82292F");

    /// <summary>Raised when the theme or hoodie colour changes (including Windows switching light/dark).</summary>
    public static event Action? Changed;

    static Theme()
    {
        SystemEvents.UserPreferenceChanged += (_, e) =>
        {
            if (e.Category == UserPreferenceCategory.General && Store.Config.Theme == "system")
                Application.Current?.Dispatcher.BeginInvoke(() => Changed?.Invoke());
        };
    }

    public static bool IsLight => Store.Config.Theme switch
    {
        "light" => true,
        "dark" => false,
        _ => WindowsAppsLight(),
    };

    public static Palette Current => IsLight ? Light : Dark;

    public static Color Hoodie => TryC(Store.Config.HoodieColour) ?? Original;

    /// <summary>"dark", "light" or "system".</summary>
    public static void SetTheme(string theme) { Store.Config.Theme = theme; Store.SaveConfig(); Changed?.Invoke(); }

    public static void SetHoodie(Color c) { Store.Config.HoodieColour = Hex(c); Store.SaveConfig(); Changed?.Invoke(); }

    public static SolidColorBrush Brush(Color c) { var b = new SolidColorBrush(c); b.Freeze(); return b; }

    static bool WindowsAppsLight()
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return k?.GetValue("AppsUseLightTheme") is int v && v == 1;
        }
        catch { return false; }
    }

    // ───────────── hoodie ─────────────

    /// <summary>Each shape's brushes as drawn in the XAML, so recolouring always starts from the original.</summary>
    static readonly Dictionary<(DependencyObject, DependencyProperty), Brush> originals = new();

    /// <summary>
    /// Repaints every hoodie-red shape under <paramref name="root"/> in <see cref="Hoodie"/>, keeping each shade's
    /// lightness relative to the original so the folds, rim and shadows still read. Parts named in
    /// <paramref name="skip"/> (props like the mug and sandwich, whose reds aren't fabric) are left alone.
    /// </summary>
    public static void PaintHoodie(DependencyObject root, ISet<string> skip)
    {
        var target = Hoodie;
        foreach (var d in Walk(root, skip))
        {
            if (d is Shape) { Paint(d, Shape.FillProperty, target); Paint(d, Shape.StrokeProperty, target); }
            else if (d is TextBlock) Paint(d, TextBlock.ForegroundProperty, target);
            else if (d is Border) Paint(d, Border.BorderBrushProperty, target);
        }
    }

    static IEnumerable<DependencyObject> Walk(DependencyObject d, ISet<string> skip)
    {
        if (d is FrameworkElement fe && skip.Contains(fe.Name)) yield break;
        yield return d;
        foreach (var child in LogicalTreeHelper.GetChildren(d).OfType<DependencyObject>())
            foreach (var x in Walk(child, skip)) yield return x;
    }

    static void Paint(DependencyObject d, DependencyProperty p, Color target)
    {
        if (!originals.TryGetValue((d, p), out var orig))
        {
            if (d.GetValue(p) is not Brush b) return;
            originals[(d, p)] = orig = b;
        }
        d.SetValue(p, Recolour(orig, target));
    }

    static Brush Recolour(Brush b, Color target)
    {
        switch (b)
        {
            case SolidColorBrush s: return Brush(Map(s.Color, target));
            case GradientBrush g:
                var copy = g.Clone();
                foreach (var stop in copy.GradientStops) stop.Color = Map(stop.Color, target);
                copy.Freeze();
                return copy;
            default: return b;   // the weave texture etc.: no colour of its own
        }
    }

    /// <summary>
    /// Moves a hoodie shade onto the new colour: same hue as the target, saturation scaled, and lightness mapped so
    /// the original mid tone lands on the target (darker shades stay darker, lighter ones lighter). Leaves
    /// anything that isn't in the hoodie's red family (greys, blacks, whites, browns) untouched.
    /// </summary>
    static Color Map(Color c, Color target)
    {
        var (h, s, l) = Hsl(c);
        var (bh, bs, bl) = Hsl(Original);
        if (s < 0.15 || HueGap(h, bh) > 14) return c;
        var (th, ts, tl) = Hsl(target);
        double nl = l <= bl ? l * tl / bl : tl + (l - bl) * (1 - tl) / (1 - bl);
        double ns = Math.Clamp(s * ts / bs, 0, 1);
        return FromHsl(th, ns, Math.Clamp(nl, 0, 1), c.A);
    }

    static double HueGap(double a, double b) { var d = Math.Abs(a - b) % 360; return d > 180 ? 360 - d : d; }

    static (double H, double S, double L) Hsl(Color c)
    {
        double r = c.R / 255.0, g = c.G / 255.0, b = c.B / 255.0;
        double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b)), l = (max + min) / 2, d = max - min;
        if (d < 1e-9) return (0, 0, l);
        double s = d / (1 - Math.Abs(2 * l - 1));
        double h = max == r ? 60 * (((g - b) / d) % 6) : max == g ? 60 * ((b - r) / d + 2) : 60 * ((r - g) / d + 4);
        return ((h + 360) % 360, s, l);
    }

    static Color FromHsl(double h, double s, double l, byte a)
    {
        double c = (1 - Math.Abs(2 * l - 1)) * s, x = c * (1 - Math.Abs(h / 60 % 2 - 1)), m = l - c / 2;
        var (r, g, b) = h < 60 ? (c, x, 0.0) : h < 120 ? (x, c, 0.0) : h < 180 ? (0.0, c, x)
                      : h < 240 ? (0.0, x, c) : h < 300 ? (x, 0.0, c) : (c, 0.0, x);
        byte B(double v) => (byte)Math.Round(Math.Clamp(v + m, 0, 1) * 255);
        return Color.FromArgb(a, B(r), B(g), B(b));
    }

    // ───────────── bits ─────────────

    static Color C(string hex) => (Color)ColorConverter.ConvertFromString(hex);
    static Color? TryC(string? hex) { try { return hex is { Length: > 0 } ? C(hex) : null; } catch { return null; } }
    public static string Hex(Color c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";
}
