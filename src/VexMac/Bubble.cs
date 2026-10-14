using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace VexMac;

/// <summary>His speech bubble: its own small see-through window, kept above his head and wholly on his screen.</summary>
public class Bubble : Window
{
    readonly TextBlock text = new() { Foreground = new SolidColorBrush(Color.Parse("#E8EAF2")), FontSize = 13, TextWrapping = TextWrapping.Wrap };
    readonly StackPanel buttons = new() { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 6, 0, 0), Spacing = 6 };

    public Bubble()
    {
        SystemDecorations = SystemDecorations.None;
        TransparencyLevelHint = new[] { WindowTransparencyLevel.Transparent };
        Background = Brushes.Transparent;
        Topmost = true; ShowInTaskbar = false; CanResize = false; ShowActivated = false;
        SizeToContent = SizeToContent.WidthAndHeight;
        Content = new Border
        {
            Background = new SolidColorBrush(Color.Parse("#F01C2030")), BorderBrush = new SolidColorBrush(Color.Parse("#8E2F36")),
            BorderThickness = new Thickness(1.5), CornerRadius = new CornerRadius(10), Padding = new Thickness(10, 6), MaxWidth = 236,
            Child = new StackPanel { Children = { text, buttons } },
        };
    }

    /// <summary>Whether it was shown with buttons (a nudge waits for one of them; a remark fades by itself).</summary>
    public bool HasButtons => buttons.Children.Count > 0;

    public void Set(string message, IReadOnlyList<(string Label, Action Act)>? options, Action onAnswer)
    {
        text.Text = message;
        buttons.Children.Clear();
        foreach (var (label, act) in options ?? Array.Empty<(string, Action)>())
        {
            var b = new Button { Content = label, Padding = new Thickness(8, 2), FontSize = 12 };
            b.Click += (_, _) => { onAnswer(); act(); };
            buttons.Children.Add(b);
        }
        buttons.IsVisible = buttons.Children.Count > 0;
    }

    /// <summary>Centred over his head (physical pixels), but kept on the screen he's on.</summary>
    public void PlaceOver(double headX, double headY, PixelRect area)
    {
        int w = (int)Math.Ceiling(Bounds.Width * RenderScaling), h = (int)Math.Ceiling(Bounds.Height * RenderScaling);
        int left = (int)Math.Clamp(headX - w / 2.0, area.X + 6, Math.Max(area.X + 6, area.Right - w - 6));
        int top = (int)Math.Max(area.Y + 6, headY - h);
        if (Position.X != left || Position.Y != top) Position = new PixelPoint(left, top);
    }
}
