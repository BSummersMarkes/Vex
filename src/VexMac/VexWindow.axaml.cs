using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;

namespace VexMac;

enum Mode { Idle, Walking, Dragging, Falling, Sleeping }

/// <summary>
/// Vex on the Mac, first version: he walks along the bottom of the screen (just above the Dock), blinks, breathes,
/// dozes off when you've been idle a while, can be picked up and dropped, holds a mug that empties (click it when you
/// refill yours) and nudges you about a stretch and some water. Ported from the Windows VexWindow, which has much
/// more (dodging windows, perching, idle activities, lunch...); those can follow. World positions are physical
/// pixels; the drawing is in DIPs and S converts.
/// </summary>
public partial class VexWindow : Window
{
    const double WinH = 260, RigCx = 130, BodyHalfW = 46, BubbleGap = 132, BodyH = 150;
    const double WalkSpeed = 70;            // DIP per second
    const double SleepAfterMinutes = 5, AwayBreakMinutes = 15;
    const double StretchEveryMinutes = 55, WaterEveryMinutes = 120, NudgeGapMinutes = 15;

    readonly Random rng = new();
    readonly Stopwatch clock = Stopwatch.StartNew();
    readonly DispatcherTimer timer;
    readonly Bubble bubble = new();
    static State St => State.Current;

    // World state (physical pixels).
    double x, y, vy, flingVx, targetX, speed;
    int facing = -1;
    Mode mode = Mode.Idle;
    double now, lastFrame, nextWander = 6, nextBlink = 2, blinkStart = -1, squashUntil, walkPhase, turn, bubbleUntil;
    bool hovering, mugMoaned, wasAway;
    DateTime nextNudgeCheck = DateTime.Now.AddSeconds(20), lastNudge = DateTime.MinValue, snoozeStretch = DateTime.MinValue;

    // Dragging.
    bool pressed; PixelPoint pressAt; double grabDx, grabDy, lastCursorT; PixelPoint lastCursor;

    // The moving parts (see VexWindow.axaml: Avalonia's scale transforms have no centre, so those are wrapped).
    readonly ScaleTransform squash = new(), facingT = new(), faceTurn = new(), eyesTurn = new(), blink = new();
    readonly RotateTransform lean = new(), mugR = new() { CenterX = 8, CenterY = 14 };
    readonly TranslateTransform bob = new(), breath = new(), hoodT = new(), faceT = new(), look = new(),
                                leftGloveT = new(), rightGloveT = new(), mugT = new(), steamT = new(), leftFootT = new(), rightFootT = new();

    double S => DesktopScaling;

    public VexWindow()
    {
        InitializeComponent();
        Resources["Weave"] = MakeWeave();

        Rig.RenderTransform = new TransformGroup { Children = { squash, facingT, lean, bob } };
        Upper.RenderTransform = breath;
        Head.RenderTransform = hoodT;
        Face.RenderTransform = new TransformGroup { Children = { Around(faceTurn, 60, 0), faceT } };
        Eyes.RenderTransform = new TransformGroup { Children = { Around(blink, 0, 51.5), Around(eyesTurn, 60, 0), look } };
        LeftGlove.RenderTransform = leftGloveT;
        RightGlove.RenderTransform = rightGloveT;
        Mug.RenderTransform = new TransformGroup { Children = { mugR, mugT } };
        Steam.RenderTransform = steamT;
        LeftFoot.RenderTransform = leftFootT;
        RightFoot.RenderTransform = rightFootT;

        Rig.PointerPressed += RigDown;
        Rig.PointerMoved += RigMove;
        Rig.PointerReleased += RigUp;
        Mug.PointerPressed += (_, e) => e.Handled = true;
        Mug.PointerReleased += (_, e) =>
        {
            e.Handled = true;
            if (e.InitialPressMouseButton == MouseButton.Left) Refill();
            else ShowMenu();
        };

        St.RollDay();
        Opened += (_, _) =>
        {
            var wa = (Screens.Primary ?? Screens.All[0]).WorkingArea;
            x = wa.Right - 320 * S;
            y = wa.Bottom;
            Place();
            bubble.Show(); bubble.Hide();   // made once, so its first real showing is sized and placed correctly
            Say(St.Coffees == 0 && St.RefilledAt == DateTime.MinValue ? "Hello. I'm Vex. Click my mug when you refill yours." : "Morning. Or whatever it is.", 6);
        };

        timer = new DispatcherTimer(TimeSpan.FromMilliseconds(33), DispatcherPriority.Render, (_, _) => Frame());
        timer.Start();
    }

    static Transform Around(ScaleTransform s, double cx, double cy) =>
        new TransformGroup { Children = { new TranslateTransform(-cx, -cy), s, new TranslateTransform(cx, cy) } };

    // ───────────────────────────── main loop ─────────────────────────────

    void Frame()
    {
        now = clock.Elapsed.TotalSeconds;
        double dt = Math.Min(0.1, now - lastFrame);
        lastFrame = now;
        var wa = ScreenAt(x).WorkingArea;
        double floor = wa.Bottom;
        hovering = Rig.IsPointerOver || bubble.IsPointerOver;

        switch (mode)
        {
            case Mode.Dragging:
                break;   // moved by the pointer (RigMove)

            case Mode.Falling:
                vy += 2600 * S * dt;
                y += vy * dt;
                x += flingVx * dt;
                flingVx *= Math.Pow(0.15, dt);
                x = Math.Clamp(x, wa.X + BodyHalfW * S, wa.Right - BodyHalfW * S);
                if (y >= floor)
                {
                    y = floor; mode = Mode.Idle; vy = 0;
                    squashUntil = now + 0.18;
                    nextWander = now + 4;
                    if (rng.NextDouble() < 0.45) Say(Pick("Rude.", "I had a mug, you know.", "...thanks.", "Wheee. (That's sarcasm.)"), 3);
                }
                break;

            case Mode.Walking:
                if (hovering) break;
                double dir = Math.Sign(targetX - x), step = speed * S * dt;
                if (Math.Abs(targetX - x) <= step) { x = targetX; mode = Mode.Idle; nextWander = now + 8 + rng.NextDouble() * 20; }
                else { x += dir * step; facing = (int)dir; walkPhase += dt * speed / 7; }
                y = floor;
                break;

            case Mode.Sleeping:
                y = floor;
                if (Platform.IdleSeconds() < 2) Wake();
                break;

            default:
                y = floor;
                if (!hovering && !bubble.HasButtons && now >= nextWander) Wander(wa);
                if (Platform.IdleSeconds() >= SleepAfterMinutes * 60) Sleep();
                break;
        }

        Animate(dt);
        if (bubbleUntil > 0 && hovering) bubbleUntil = Math.Max(bubbleUntil, now + 2);
        if (bubbleUntil > 0 && now > bubbleUntil) HideBubble();
        Place();
        if (bubble.IsVisible) bubble.PlaceOver(x, y - BubbleGap * S, wa);
        if (DateTime.Now >= nextNudgeCheck) { nextNudgeCheck = DateTime.Now.AddSeconds(15); Nudges(); }

        // Smooth while something moves; otherwise a gentler pace (breathing at 10 fps looks the same, and costs less).
        bool lively = mode is Mode.Walking or Mode.Dragging or Mode.Falling || pressed || hovering || blinkStart >= 0
                      || now >= nextBlink - 0.1 || now < squashUntil || Math.Abs(turn - (mode == Mode.Walking ? 25 : 0)) > 0.5;
        var interval = TimeSpan.FromMilliseconds(lively ? 33 : 100);
        if (timer.Interval != interval) timer.Interval = interval;
    }

    Screen ScreenAt(double px)
    {
        var all = Screens.All;
        foreach (var s in all) if (s.WorkingArea.X <= px && px < s.WorkingArea.Right) return s;
        return all.OrderBy(s => Math.Min(Math.Abs(s.WorkingArea.X - px), Math.Abs(s.WorkingArea.Right - px))).First();
    }

    void Place()
    {
        var p = new PixelPoint((int)Math.Round(x - RigCx * S), (int)Math.Round(y - WinH * S));
        if (Position != p) Position = p;
    }

    // ───────────────────────────── behaviours ─────────────────────────────

    void Wander(PixelRect wa)
    {
        nextWander = now + 10 + rng.NextDouble() * 25;
        if (rng.NextDouble() < 0.35) { facing = -facing; return; }   // just look the other way
        double lo = wa.X + (BodyHalfW + 12) * S, hi = wa.Right - (BodyHalfW + 12) * S;
        if (hi <= lo) return;
        targetX = Math.Clamp(x + (rng.NextDouble() * 2 - 1) * 380 * S, lo, hi);
        speed = WalkSpeed;
        mode = Mode.Walking;
    }

    void Sleep() { mode = Mode.Sleeping; Zzz.IsVisible = true; if (!bubble.HasButtons) HideBubble(); }

    void Wake(bool quiet = false)
    {
        mode = Mode.Idle; Zzz.IsVisible = false; blinkStart = now; nextWander = now + 6;
        if (!quiet && rng.NextDouble() < 0.3) Say(Pick("I wasn't asleep. I was resting my eyes.", "Oh. You're back.", "Welcome back. Nothing happened. I checked."), 4);
    }

    internal void Refill()
    {
        St.RollDay();
        St.Coffees++;
        St.RefilledAt = DateTime.Now;
        St.Save();
        mugMoaned = false;
        squashUntil = now + 0.15;
        int n = St.Coffees;
        Say(n switch
        {
            1 => "First one. The day may now begin.",
            2 => "Number two. Sensible.",
            3 => "Three. Maybe some water next time?",
            4 => "Four. I'm not judging. I'm counting, which is worse.",
            _ => $"That's {n}. Your heart has filed a complaint.",
        }, 4);
    }

    internal void TakingBreak()
    {
        St.SittingSince = DateTime.Now; St.Save();
        Say(Pick("Go on then. I'll guard the desk.", "Off you go. I'll be here. Obviously."), 4);
    }

    internal bool Asleep => mode == Mode.Sleeping;
    internal void ToggleNap() { if (mode == Mode.Sleeping) Wake(quiet: true); else Sleep(); }

    // ───────────────────────────── nudges ─────────────────────────────

    /// <summary>
    /// Every 15 s: a stretch after StretchEveryMinutes of sitting, water every couple of hours. At most one nudge per
    /// NudgeGapMinutes, never while you're away. Unlike the Windows Vex, a long spell away (AwayBreakMinutes) counts
    /// as a break here: there's no lab to be working in.
    /// </summary>
    void Nudges()
    {
        St.RollDay();
        double idle = Platform.IdleSeconds();
        bool away = idle >= 120;
        if (idle >= AwayBreakMinutes * 60) wasAway = true;
        else if (wasAway && idle < 5)
        {
            wasAway = false;
            St.SittingSince = DateTime.Now; St.Save();
        }
        if (away || bubble.HasButtons || mode is Mode.Dragging or Mode.Falling) return;
        var t = DateTime.Now;
        if (t - lastNudge < TimeSpan.FromMinutes(NudgeGapMinutes)) return;

        var sat = t - St.SittingSince;
        if (sat >= TimeSpan.FromMinutes(StretchEveryMinutes) && t >= snoozeStretch)
        {
            lastNudge = t;
            Nudge($"You've been sat for {(int)sat.TotalMinutes} minutes. Stand up, stretch. I'll guard the desk.",
                  ("Taking a break", TakingBreak), ("Not now", () => snoozeStretch = DateTime.Now.AddMinutes(10)));
            return;
        }
        if (t - St.LastWater >= TimeSpan.FromMinutes(WaterEveryMinutes))
        {
            lastNudge = t;
            Nudge(St.Coffees >= 2 ? $"{St.Coffees} coffees and, I suspect, no water. Fix one of those." : "Water. The boring drink. Have some.",
                  ("Drinking", () => { St.LastWater = DateTime.Now; St.Save(); }),
                  ("Later", () => { St.LastWater = DateTime.Now.AddMinutes(-WaterEveryMinutes + 15); St.Save(); }));
        }
    }

    void Nudge(string text, params (string, Action)[] options)
    {
        if (mode == Mode.Sleeping) Wake(quiet: true);
        if (mode == Mode.Walking) mode = Mode.Idle;
        squashUntil = now + 0.25;   // a little hop to catch your eye
        ShowBubble(text, double.PositiveInfinity, options);
    }

    // ───────────────────────────── bubble ─────────────────────────────

    public void Say(string text, double seconds = 6)
    {
        if (bubble.HasButtons && bubble.IsVisible) return;   // don't bury a nudge with a remark
        ShowBubble(text, seconds, null);
    }

    void ShowBubble(string text, double seconds, IReadOnlyList<(string, Action)>? options)
    {
        bubble.Set(text, options, HideBubble);
        if (!bubble.IsVisible) bubble.Show();
        bubble.UpdateLayout();
        bubble.PlaceOver(x, y - BubbleGap * S, ScreenAt(x).WorkingArea);
        bubbleUntil = now + seconds;
    }

    void HideBubble() { bubble.Set("", null, () => { }); bubble.Hide(); bubbleUntil = 0; }

    // ───────────────────────────── mouse ─────────────────────────────

    void RigDown(object? sender, PointerPressedEventArgs e)
    {
        var props = e.GetCurrentPoint(Rig).Properties;
        if (props.IsRightButtonPressed) { e.Handled = true; ShowMenu(); return; }
        if (!props.IsLeftButtonPressed) return;
        pressed = true;
        pressAt = this.PointToScreen(e.GetPosition(this));
        e.Pointer.Capture(Rig);
        e.Handled = true;
    }

    void RigMove(object? sender, PointerEventArgs e)
    {
        if (!pressed) return;
        var c = this.PointToScreen(e.GetPosition(this));
        if (mode != Mode.Dragging)
        {
            if (Math.Abs(c.X - pressAt.X) + Math.Abs(c.Y - pressAt.Y) < 6 * S) return;
            if (mode == Mode.Sleeping) Wake(quiet: true);
            mode = Mode.Dragging;
            if (!bubble.HasButtons) HideBubble();
            grabDx = c.X - x; grabDy = c.Y - y;
            lastCursor = c; lastCursorT = now; flingVx = 0;
        }
        x = c.X - grabDx; y = c.Y - grabDy;
        if (now - lastCursorT > 0.03) { flingVx = (c.X - lastCursor.X) / (now - lastCursorT); lastCursor = c; lastCursorT = now; }
        Place();
    }

    void RigUp(object? sender, PointerReleasedEventArgs e)
    {
        e.Pointer.Capture(null);
        if (!pressed) return;
        pressed = false;
        e.Handled = true;
        if (mode == Mode.Dragging)
        {
            mode = Mode.Falling; vy = 0;
            flingVx = Math.Clamp(flingVx, -2500 * S, 2500 * S);
            var floor = ScreenAt(x).WorkingArea.Bottom;
            if (y > floor) y = floor;
            return;
        }
        // Just a click: how things stand.
        if (mode == Mode.Sleeping) { Wake(quiet: true); return; }
        int mins = (int)(DateTime.Now - St.SittingSince).TotalMinutes;
        Say($"Sat for {mins} minute{(mins == 1 ? "" : "s")}. Coffee: {St.Coffees} today. Right-click me for more.", 5);
    }

    void ShowMenu()
    {
        MenuItem Item(string header, Action act) { var m = new MenuItem { Header = header }; m.Click += (_, _) => act(); return m; }
        var items = new List<object>
        {
            Item("I refilled my coffee", Refill),
            Item("Taking a break", TakingBreak),
            Item(Asleep ? "Wake up" : "Have a nap", ToggleNap),
        };
        if (Platform.CanStartAtLogin)
        {
            var login = new MenuItem { Header = "Start at login", ToggleType = MenuItemToggleType.CheckBox, IsChecked = Platform.StartsAtLogin };
            login.Click += (_, _) => Platform.SetStartAtLogin(!Platform.StartsAtLogin);
            items.Add(login);
        }
        items.Add(new Separator());
        items.Add(Item("Quit", App.Quit));
        new ContextMenu { ItemsSource = items }.Open(Rig);
    }

    string Pick(params string[] lines) => lines[rng.Next(lines.Length)];

    // ───────────────────────────── animation ─────────────────────────────

    /// <summary>Fine fabric grain: random dark specks, tiled, laid over the cloth shapes (as on Windows).</summary>
    static IBrush MakeWeave()
    {
        const int n = 96;
        var bmp = new WriteableBitmap(new PixelSize(n, n), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul);
        var r = new Random(2);
        using (var fb = bmp.Lock())
        {
            var px = new byte[n * n * 4];
            for (int i = 0; i < n * n; i++)
            {
                double a = Math.Max(0, 0.5 * r.NextDouble() - 0.18);
                px[i * 4 + 3] = (byte)(a * 255);   // premultiplied black: only alpha
            }
            for (int row = 0; row < n; row++)
                System.Runtime.InteropServices.Marshal.Copy(px, row * n * 4, fb.Address + row * fb.RowBytes, n * 4);
        }
        return new ImageBrush(bmp)
        {
            TileMode = TileMode.Tile, Stretch = Stretch.None,
            DestinationRect = new RelativeRect(0, 0, n, n, RelativeUnit.Absolute),
        };
    }

    void Animate(double dt)
    {
        facingT.ScaleX = facing;
        bool walking = mode == Mode.Walking && !hovering, sleeping = mode == Mode.Sleeping;

        // Walk cycle: feet shuffle, body bobs, free mitten swings.
        double sw = walking ? Math.Sin(walkPhase) : 0;
        leftFootT.X = sw * 5; leftFootT.Y = walking ? -Math.Max(0, sw) * 4 : 0;
        rightFootT.X = -sw * 5; rightFootT.Y = walking ? -Math.Max(0, -sw) * 4 : 0;
        leftGloveT.X = -sw * 4; leftGloveT.Y = walking ? Math.Abs(sw) * -1.5 : Math.Sin(now * 1.7) * 0.6;
        rightGloveT.Y = mugT.Y = walking ? Math.Abs(Math.Cos(walkPhase)) * -1.5 : 0;
        double br = Math.Sin(now * (sleeping ? 1.2 : 2.0));
        breath.Y = walking ? -Math.Abs(sw) * 2.5 : br * (sleeping ? 1.5 : 0.8) + (sleeping ? 4 : 0);

        // Squash on landing, refills and nudges.
        double sq = now < squashUntil ? 1 - 0.08 * Math.Sin((squashUntil - now) / 0.18 * Math.PI) : 1;
        squash.ScaleY = sq; squash.ScaleX = 2 - sq;
        bob.Y = mode == Mode.Dragging ? Math.Sin(now * 9) * 1.5 : 0;

        // Eyes: blink, sleep, squint while dangling.
        double eyeScale = 1;
        if (sleeping) eyeScale = 0.12;
        else
        {
            if (blinkStart < 0 && now >= nextBlink) blinkStart = now;
            if (blinkStart >= 0)
            {
                double b = (now - blinkStart) / 0.14;
                if (b >= 1) { blinkStart = -1; nextBlink = now + (rng.NextDouble() < 0.2 ? 0.25 : 2 + rng.NextDouble() * 4); }
                else eyeScale = Math.Abs(1 - 2 * b) * 0.9 + 0.1;
            }
            if (mode == Mode.Dragging) eyeScale = Math.Min(eyeScale, 0.6);
        }
        blink.ScaleY = mode == Mode.Falling ? 1.25 : eyeScale;

        // Head turn: the face and eyes squash and slide inside the hood, the hood shifts a touch.
        double target = walking ? 25 : 0;
        turn += (target - turn) * Math.Min(1, dt * 6);
        double th = turn * Math.PI / 180, sn = Math.Sin(th);
        faceTurn.ScaleX = eyesTurn.ScaleX = Math.Cos(th);
        faceT.X = 14 * sn; look.X = 11 * sn; hoodT.X = 1.5 * sn;

        // Mug drains over 45 minutes; steam while it's fresh.
        double mins = (DateTime.Now - St.RefilledAt).TotalMinutes;
        double level = Math.Clamp(1 - mins / 45, 0, 1), h = 14.5 * level;
        Coffee.Height = h;
        Canvas.SetTop(Coffee, 6.25 + 14.5 - h);
        bool steaming = level > 0 && mins < 15;
        Steam.Opacity += ((steaming ? 0.55 + 0.25 * Math.Sin(now * 2.3) : 0) - Steam.Opacity) * Math.Min(1, dt * 3);
        steamT.Y = -((now * 6) % 4);
        if (level == 0 && !mugMoaned && St.RefilledAt > DateTime.MinValue && mode == Mode.Idle)
        {
            mugMoaned = true;
            Say("Mug's empty. Just mentioning it.", 6);
        }
    }
}
