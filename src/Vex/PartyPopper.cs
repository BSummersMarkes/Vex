using System;
using System.Collections.Generic;
using System.IO;
using System.Media;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using Path = System.Windows.Shapes.Path;
using Forms = System.Windows.Forms;

namespace Vex;

/// <summary>
/// The birthday party popper (VexBirthday.cs): a pop, then confetti and curly streamers burst from beside Vex and
/// flutter down over a patch of the desktop for ConfettiSeconds, then the window closes itself. It's a separate
/// see-through window that's click-through everywhere (WS_EX_TRANSPARENT), so whatever is underneath can be clicked
/// and typed in the whole time; never focused, not in Alt-Tab, and kept out of screen captures like Vex.
/// </summary>
sealed class PartyPopper : Window
{
    // The patch of desktop it covers, in DIPs; his popper sits low down in it so the confetti has room to go up and fall.
    const double W = 900, H = 600, OriginDown = 0.72;
    const double Gravity = 620;   // DIP/s²

    static readonly Color[] Colours =
    {
        Color.FromRgb(0xE8, 0xB3, 0x3C), Color.FromRgb(0x3F, 0xA7, 0xA0), Color.FromRgb(0x5B, 0x7F, 0xD6), Color.FromRgb(0xD8, 0x38, 0x3C),
        Color.FromRgb(0xE3, 0xA3, 0xAE), Color.FromRgb(0xF4, 0xF2, 0xEE), Color.FromRgb(0x6D, 0xB3, 0x4F), Color.FromRgb(0x8E, 0x6B, 0xC8),
    };
    static byte[]? popWav;

    /// <summary>One bit of confetti, or the head of a streamer (which drags a ribbon of where it's been).</summary>
    sealed class Bit
    {
        public double X, Y, Vx, Vy, Drag, Spin, Angle, Flip, Phase, Sway;
        public UIElement Shape = null!;
        public TranslateTransform Move = new();
        public RotateTransform Turn = new();
        public ScaleTransform Tumble = new();
        public Polyline? Ribbon;
        public readonly List<Point> Trail = new();
    }

    readonly Canvas canvas = new() { IsHitTestVisible = false };
    readonly List<Bit> bits = new();
    readonly Random rng = new();
    readonly DispatcherTimer timer;
    readonly double life = Math.Max(1, Store.Config.ConfettiSeconds);
    readonly System.Diagnostics.Stopwatch clock = System.Diagnostics.Stopwatch.StartNew();
    double last;
    Canvas? popper;
    Ellipse? flash;

    /// <summary>
    /// Pop one off. <paramref name="originX"/>, <paramref name="originY"/> are where he holds it, in physical screen
    /// pixels like the rest of VexWindow; <paramref name="dir"/> (±1) is the side it's aimed at.
    /// </summary>
    public static void Fire(double originX, double originY, int dir, Forms.Screen scr)
    {
        try { new PartyPopper().Burst(originX, originY, dir, scr); }
        catch (Exception ex) { Store.Log($"Party popper: {ex.Message}"); }
    }

    PartyPopper()
    {
        WindowStyle = WindowStyle.None; AllowsTransparency = true; Background = Brushes.Transparent;
        Topmost = true; ShowInTaskbar = false; ShowActivated = false; ResizeMode = ResizeMode.NoResize;
        Width = W; Height = H; Left = -32000; Top = -32000; Title = "Vex confetti";
        IsHitTestVisible = false; Focusable = false;
        Content = canvas;
        SourceInitialized += (_, _) =>
        {
            var h = new WindowInteropHelper(this).Handle;
            var ex = Native.GetWindowLongPtr(h, Native.GWL_EXSTYLE);
            Native.SetWindowLongPtr(h, Native.GWL_EXSTYLE, ex | Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE | Native.WS_EX_TRANSPARENT);
            if (Store.Config.HideFromScreenCapture) Native.SetWindowDisplayAffinity(h, Native.WDA_EXCLUDEFROMCAPTURE);
        };
        timer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(33) };
        timer.Tick += (_, _) => Step();
    }

    void Burst(double ox, double oy, int dir, Forms.Screen scr)
    {
        Show();
        var hwnd = new WindowInteropHelper(this).Handle;
        // Place it over him, kept on his monitor. Moving it there can change its size in pixels (another monitor's
        // scaling), so it's measured again after the move and placed a second time if need be.
        var wa = scr.WorkingArea;
        Native.RECT r = default;
        for (int pass = 0; pass < 2; pass++)
        {
            Native.GetWindowRect(hwnd, out r);
            int w = r.Right - r.Left, h = r.Bottom - r.Top;
            int left = (int)Math.Round(Math.Clamp(ox - w / 2.0, wa.Left, Math.Max(wa.Left, wa.Right - w)));
            int top = (int)Math.Round(Math.Clamp(oy - h * OriginDown, wa.Top, Math.Max(wa.Top, wa.Bottom - h)));
            Native.SetWindowPos(hwnd, Native.HWND_TOPMOST, left, top, 0, 0, Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
        }
        Native.GetWindowRect(hwnd, out r);
        double k = W / Math.Max(1, r.Right - r.Left);   // DIPs per pixel in here
        double x0 = (ox - r.Left) * k, y0 = (oy - r.Top) * k;

        // Aimed up and away from him; the spray fans out around that.
        double aim = -Math.PI / 2 + dir * 0.45;
        int pieces = Math.Clamp(Store.Config.ConfettiPieces, 0, 400);
        for (int i = 0; i < pieces; i++) AddBit(x0, y0, aim, ribbon: false);
        for (int i = 0; i < pieces / 10; i++) AddBit(x0, y0, aim, ribbon: true);
        AddPopper(x0, y0, aim);

        if (Store.Config.KnockSound && Services.Hub.InCall == null) PlayPop();
        Step();
        timer.Start();
    }

    void AddBit(double x0, double y0, double aim, bool ribbon)
    {
        var colour = Colours[rng.Next(Colours.Length)];
        double a = aim + (rng.NextDouble() * 2 - 1) * (ribbon ? 0.55 : 0.85);
        double v = ribbon ? 520 + rng.NextDouble() * 260 : 300 + rng.NextDouble() * 420;
        var b = new Bit
        {
            X = x0, Y = y0, Vx = Math.Cos(a) * v, Vy = Math.Sin(a) * v,
            // Paper catches the air: confetti slows quickly and drifts down; streamer heads carry further.
            Drag = ribbon ? 1.3 : 1.7 + rng.NextDouble() * 0.6,
            Spin = (rng.NextDouble() * 2 - 1) * 540, Angle = rng.NextDouble() * 360,
            Flip = 4 + rng.NextDouble() * 8, Phase = rng.NextDouble() * Math.PI * 2, Sway = 20 + rng.NextDouble() * 40,
        };
        if (ribbon)
        {
            // A ribbon is just the trail of where its head has been; the head weaves, so the trail curls.
            b.Ribbon = new Polyline
            {
                Stroke = new SolidColorBrush(colour), StrokeThickness = 2.2, StrokeLineJoin = PenLineJoin.Round,
                StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round,
            };
            b.Sway = 120 + rng.NextDouble() * 80;
            b.Shape = b.Ribbon;
            canvas.Children.Add(b.Ribbon);
        }
        else
        {
            // Mostly little paper rectangles, a few round dots; each tumbles (Tumble squashes it as it turns over).
            Shape s = rng.NextDouble() < 0.25
                ? new Ellipse { Width = 4.5, Height = 4.5 }
                : new Rectangle { Width = 3.5 + rng.NextDouble() * 2, Height = 6 + rng.NextDouble() * 3 };
            s.Fill = new SolidColorBrush(colour);
            Canvas.SetLeft(s, -s.Width / 2); Canvas.SetTop(s, -s.Height / 2);
            s.RenderTransform = new TransformGroup { Children = { b.Tumble, b.Turn, b.Move } };
            b.Shape = s;
            canvas.Children.Add(s);
        }
        bits.Add(b);
    }

    /// <summary>The popper itself: a little striped paper cone, its mouth at the burst, with a puff as it goes off.</summary>
    void AddPopper(double x0, double y0, double aim)
    {
        popper = new Canvas
        {
            RenderTransform = new TransformGroup
            {
                Children = { new RotateTransform(aim * 180 / Math.PI), new TranslateTransform(x0, y0) },
            },
        };
        var cone = Geometry.Parse("M -20,-1.6 L 0,-6 L 0,6 L -20,1.6 Z");
        popper.Children.Add(new Path
        {
            Data = cone, Stroke = new SolidColorBrush(Color.FromRgb(0x9C, 0x75, 0x20)), StrokeThickness = 0.6,
            Fill = new LinearGradientBrush(Color.FromRgb(0xF2, 0xC9, 0x5C), Color.FromRgb(0xB0, 0x82, 0x24), 90),
        });
        popper.Children.Add(new Path
        {
            Data = Geometry.Parse("M -14,-3 L -10,-3.9 L -10,3.9 L -14,3 Z M -6,-4.8 L -2,-5.7 L -2,5.7 L -6,4.8 Z"),
            Fill = new SolidColorBrush(Color.FromRgb(0xD8, 0x38, 0x3C)),
        });
        flash = new Ellipse { Width = 22, Height = 22, Fill = new RadialGradientBrush(Color.FromArgb(0xD0, 0xFF, 0xFF, 0xF0), Color.FromArgb(0, 0xFF, 0xFF, 0xF0)) };
        Canvas.SetLeft(flash, -6); Canvas.SetTop(flash, -11);
        popper.Children.Add(flash);
        canvas.Children.Insert(0, popper);
    }

    void Step()
    {
        double t = clock.Elapsed.TotalSeconds, dt = Math.Min(0.1, t - last);
        last = t;
        if (t >= life) { timer.Stop(); Close(); return; }

        // Everything fades over the last second, so nothing just blinks out.
        canvas.Opacity = Math.Clamp((life - t) / 1.0, 0, 1);
        if (popper != null)
        {
            popper.Opacity = Math.Clamp(1.4 - t, 0, 1);   // the empty popper goes once the burst is under way
            if (flash != null) flash.Opacity = Math.Clamp(1 - t / 0.25, 0, 1);
        }

        foreach (var b in bits)
        {
            double damp = Math.Exp(-b.Drag * dt);
            b.Vx *= damp; b.Vy = b.Vy * damp + Gravity * dt;
            b.X += (b.Vx + Math.Sin(t * (b.Ribbon != null ? 9 : 3) + b.Phase) * b.Sway) * dt;
            b.Y += b.Vy * dt;
            if (b.Ribbon != null)
            {
                b.Trail.Add(new Point(b.X, b.Y));
                if (b.Trail.Count > 16) b.Trail.RemoveAt(0);
                b.Ribbon.Points = new PointCollection(b.Trail);
            }
            else
            {
                b.Angle += b.Spin * dt;
                b.Turn.Angle = b.Angle;
                b.Tumble.ScaleY = Math.Cos(t * b.Flip + b.Phase);
                b.Move.X = b.X; b.Move.Y = b.Y;
            }
        }
    }

    static void PlayPop()
    {
        try
        {
            popWav ??= MakePopWav();
            using var player = new SoundPlayer(new MemoryStream(popWav));
            player.Play(); // async; the stream is read up front
        }
        catch (Exception ex) { Store.Log($"Pop sound: {ex.Message}"); }
    }

    /// <summary>
    /// A soft party-popper pop: a sharp crack over a low thump, then a quiet patter of paper. Synthesised, like the
    /// knock, so there's no sound file to ship.
    /// </summary>
    static byte[] MakePopWav()
    {
        const int rate = 22050;
        int n = (int)(rate * 0.8);
        var samples = new short[n];
        var rnd = new Random(11);
        for (int i = 0; i < n; i++)
        {
            double t = i / (double)rate;
            double crack = (rnd.NextDouble() * 2 - 1) * Math.Exp(-t * 90) * 0.7;
            double thump = Math.Sin(2 * Math.PI * (140 - 60 * Math.Min(1, t * 12)) * t) * Math.Exp(-t * 35) * 0.5;
            double v = (crack + thump) * Math.Min(1, t * 2000);   // a 0.5 ms attack, so the start doesn't click
            samples[i] = (short)Math.Clamp(v * 11000, short.MinValue, short.MaxValue);
        }
        // Paper landing: a few dozen tiny, quiet ticks scattered over the rest.
        for (int k = 0; k < 40; k++)
        {
            int start = (int)((0.12 + rnd.NextDouble() * 0.6) * rate);
            for (int i = 0; i < 90 && start + i < n; i++)
                samples[start + i] = (short)Math.Clamp(samples[start + i] + (rnd.NextDouble() * 2 - 1) * Math.Exp(-i / 18.0) * 1400, short.MinValue, short.MaxValue);
        }
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        static byte[] Tag(string s) => System.Text.Encoding.ASCII.GetBytes(s);   // (not "RIFF"u8: the lite build's .NET Framework lacks it)
        w.Write(Tag("RIFF")); w.Write(36 + n * 2); w.Write(Tag("WAVE"));
        w.Write(Tag("fmt ")); w.Write(16); w.Write((short)1); w.Write((short)1); w.Write(rate); w.Write(rate * 2); w.Write((short)2); w.Write((short)16);
        w.Write(Tag("data")); w.Write(n * 2);
        foreach (var s in samples) w.Write(s);
        return ms.ToArray();
    }
}
