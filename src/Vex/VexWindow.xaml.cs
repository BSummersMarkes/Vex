using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using Microsoft.Win32;
using Forms = System.Windows.Forms;

namespace Vex;

enum Mode { Idle, Walking, Dragging, Falling, Sleeping, Hopping }

/// <summary>
/// The character window. All world positions are in physical screen pixels; the drawing is in DIPs
/// and S (the current monitor's scale) converts between them.
/// </summary>
public partial class VexWindow : Window
{
    // Window layout in DIPs (matches VexWindow.xaml).
    const double WinW = 260, WinH = 260;
    const double RigCx = 130;              // horizontal centre of the character within the window
    const double BubbleGap = 132;          // bubble's bottom edge, measured up from his feet
    const double BodyHalfW = 46, BodyH = 150;
    const double EyeY = 98;                // eyes, measured up from his feet
    const double PeekDrop = 86;            // how far he sinks so only the hood and eyes show

    static readonly HashSet<string> IgnoredClasses = new()
    {
        "Progman", "WorkerW", "Shell_TrayWnd", "Shell_SecondaryTrayWnd", "NotifyIconOverflowWindow",
        "TopLevelWindowForOverflowXamlIsland", "Windows.UI.Core.CoreWindow", "XamlExplorerHostIslandWindow",
        "ForegroundStaging", "MultitaskingViewFrame", "TaskSwitcherWnd", "#32768",
    };

    readonly Random rng = new();
    readonly Stopwatch clock = Stopwatch.StartNew();
    readonly DispatcherTimer timer;
    readonly uint ownPid = (uint)Environment.ProcessId;
    IntPtr hwnd;
    Forms.Screen[] screens = Forms.Screen.AllScreens;
    ChatWindow? chat;

    // World state (physical pixels).
    double x, y;              // centre of his feet
    double vy;
    double targetX, speed;
    int facing = -1;
    bool peeking, hidden, autoHidden, thinking, napping;
    Action? bubbleTimeout;
    double turn;
    Window? bubbleWin;
    IntPtr bubbleHwnd;
    bool hovering;   // napping: asked to nap, so ignore keyboard/mouse activity
    Mode mode = Mode.Idle;

    // Timers, in seconds since start.
    double now, lastFrame, nextWander, nextBlink, blinkStart = -1, nextDodgeCheck, nextTopmost, bubbleUntil;
    double walkPhase, squashUntil;
    double hopStart; double hopToX; bool hopPeek; bool hopMoved;
    int lastWinX = int.MinValue, lastWinY = int.MinValue;
    bool mugMoaned;

    // Dragging.
    bool pressed; Point pressAt; double grabDx, grabDy; Point lastCursor; double lastCursorT; double flingVx;

    double S => hwnd == IntPtr.Zero ? 1 : Native.GetDpiForWindow(hwnd) / 96.0;

    public VexWindow()
    {
        InitializeComponent();
        Resources["Weave"] = MakeWeave();
        MakeBubbleWindow();
        ApplyAppearance();
        Theme.Changed += ApplyAppearance;
        Store.RollDay();
        SourceInitialized += (_, _) =>
        {
            hwnd = new WindowInteropHelper(this).Handle;
            // Tool window: not in Alt-Tab. No-activate: clicking him never steals focus from your work.
            var ex = Native.GetWindowLongPtr(hwnd, Native.GWL_EXSTYLE);
            Native.SetWindowLongPtr(hwnd, Native.GWL_EXSTYLE, ex | Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE);
            // Never shows up in screen shares or screenshots.
            if (Store.Config.HideFromScreenCapture) Native.SetWindowDisplayAffinity(hwnd, Native.WDA_EXCLUDEFROMCAPTURE);
            var wa = Forms.Screen.PrimaryScreen!.WorkingArea;
            x = wa.Right - 320 * S;
            y = wa.Bottom;
            Place(force: true);
            Services.Hub.Start(this);
            RegisterQuickChat();
        };
        SystemEvents.DisplaySettingsChanged += (_, _) => Dispatcher.Invoke(() => screens = Forms.Screen.AllScreens);

        Rig.MouseLeftButtonDown += RigDown;
        Rig.MouseMove += RigMove;
        Rig.MouseLeftButtonUp += RigUp;
        Rig.MouseRightButtonUp += (_, e) => { ShowMenu(); e.Handled = true; };
        Mug.MouseLeftButtonDown += (_, e) => e.Handled = true;
        Mug.MouseLeftButtonUp += (_, e) => { Refill(); chat?.Dismiss(); e.Handled = true; };   // a click away from the chat
        Mug.ToolTip = "";
        Mug.ToolTipOpening += (_, _) => Mug.ToolTip = $"Coffee #{Store.State.Coffees} today. Click when you refill yours.";
        Bubble.MouseLeftButtonUp += (_, _) => { if (BubbleButtons.Children.Count == 0) HideBubble(); };

        nextWander = 6; nextBlink = 2;
        timer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(33) };
        timer.Tick += (_, _) => Frame();
        timer.Start();
    }

    // ───────────────────────────── main loop ─────────────────────────────

    void Frame()
    {
        now = clock.Elapsed.TotalSeconds;
        double dt = Math.Min(0.1, now - lastFrame);
        lastFrame = now;
        if (hwnd == IntPtr.Zero) return;

        var scr = ScreenAt(x);
        double floor = PerchFloor() ?? scr.WorkingArea.Bottom; // top of the window he's standing on, or the taskbar

        // Mouse over him or his bubble: stand still so it can be read and clicked.
        hovering = Rig.IsMouseOver || Bubble.IsMouseOver;

        if (now >= nextDodgeCheck && !hovering && !AtLunch && mode is not (Mode.Dragging or Mode.Falling or Mode.Hopping))
        {
            nextDodgeCheck = now + 0.3;
            CheckSurroundings(scr);
        }

        switch (mode)
        {
            case Mode.Dragging:
                Native.GetCursorPos(out var c);
                x = c.X - grabDx; y = c.Y - grabDy;
                if (now - lastCursorT > 0.03)
                {
                    flingVx = (c.X - lastCursor.X) / (now - lastCursorT);
                    lastCursor = new Point(c.X, c.Y); lastCursorT = now;
                }
                break;

            case Mode.Falling:
                FallStep(scr, dt); // may land on a window (VexPerch.cs)
                break;

            case Mode.Walking:
                if (hovering) { Settle(floor, dt); break; }
                double dir = Math.Sign(targetX - x);
                double step = speed * S * dt;
                if (Math.Abs(targetX - x) <= step) { x = targetX; mode = Mode.Idle; nextWander = now + 8 + rng.NextDouble() * 20; }
                else { x += dir * step; facing = (int)dir; walkPhase += dt * speed / 7; }
                Settle(floor, dt);
                break;

            case Mode.Hopping:
                // Fade out, jump, fade in. Used for long moves and monitor changes.
                double t = now - hopStart;
                if (t < 0.15) Opacity = 1 - t / 0.15;
                else if (!hopMoved)
                {
                    hopMoved = true; x = hopToX; peeking = hopPeek;
                    y = ScreenAt(x).WorkingArea.Bottom + (peeking ? PeekDrop * S : 0);
                    Place(force: true);
                }
                else if (t < 0.35) Opacity = (t - 0.15) / 0.2;
                else { Opacity = hidden ? 0 : 1; mode = Mode.Idle; nextWander = now + 10; }
                break;

            case Mode.Sleeping:
                Settle(floor, dt);
                if (!napping && Native.IdleTime().TotalSeconds < 2) Wake();
                break;

            default: // Idle
                Settle(floor, dt);
                if (!peeking && !thinking && !hovering && chat == null && currentNudge == null && act == Act.None && pendingAct == Act.None && now >= nextWander) Wander(scr); // no strolling off mid-conversation
                if (!peeking && !AtLunch && Native.IdleTime().TotalMinutes >= Store.Config.SleepAfterMinutes) Sleep();
                break;
        }

        UpdateNudge();
        UpdateActs(scr);
        Animate(dt);
        if (bubbleUntil > 0 && hovering) bubbleUntil = Math.Max(bubbleUntil, now + 2);
        if (bubbleUntil > 0 && now > bubbleUntil) BubbleExpired();
        Place();
        PlaceBubble(ScreenAt(x)); // same frame as Vex's own move, so the two stay locked together
        chat?.Follow(x, y - BodyH * S, ScreenAt(x));
    }

    void Settle(double floor, double dt)
    {
        double want = floor + (peeking ? PeekDrop * S : 0);
        y += (want - y) * Math.Min(1, dt * 8);
        if (Math.Abs(want - y) < 0.5) y = want;
    }

    // ───────────────────────────── staying out of the way ─────────────────────────────

    void CheckSurroundings(Forms.Screen scr)
    {
        // Monitor unplugged under him? Go home.
        if (!screens.Any(s => s.WorkingArea.Left <= x && x < s.WorkingArea.Right))
        {
            var home = Forms.Screen.PrimaryScreen!.WorkingArea;
            HopTo(home.Right - 320 * S, false);
            return;
        }
        if (Perched) { CheckPerch(); return; }

        var fg = ForegroundRect();
        bool covered = fg is { } r && Overlaps(r, x, scr.WorkingArea.Bottom);

        // A presentation or video full screen with nowhere else to stand: vanish until it's over.
        bool vanish = Services.Hub.FullScreen && covered && FindSpot(fg, scr) == null;
        if (vanish != autoHidden) SetAutoHidden(vanish);
        if (vanish) return;

        if (peeking)
        {
            // Is there room to stand up again?
            var spot = FindSpot(fg, scr);
            if (spot is { } s && Walkable(x, s))
            {
                peeking = false;
                if (Math.Abs(s - x) > 1) WalkTo(s, Store.Config.WalkSpeed * 2);
            }
            return;
        }

        if (mode == Mode.Walking && fg is { } rw && Overlaps(rw, targetX, scr.WorkingArea.Bottom))
            mode = Mode.Idle; // destination got covered; re-plan below

        if (!covered) return;
        if (mode == Mode.Sleeping) Wake();

        // Stroll out of the way (across monitors if the taskbars join up). Never teleport.
        if (FindSpot(fg, scr) is { } tx && Walkable(x, tx))
        {
            if (mode != Mode.Walking || targetX != tx) WalkTo(tx, Store.Config.WalkSpeed * 2);
        }
        else
        {
            // Nowhere to go: duck down where he is so just his eyes peek over the taskbar.
            mode = Mode.Idle;
            peeking = true;
        }
    }

    /// <summary>True if there's continuous floor between two points (no gaps between monitors).</summary>
    bool Walkable(double from, double to)
    {
        double lo = Math.Min(from, to), hi = Math.Max(from, to);
        for (double px = lo; px <= hi; px += 10)
            if (!screens.Any(s => s.WorkingArea.Left <= px && px < s.WorkingArea.Right)) return false;
        return true;
    }

    Native.RECT? ForegroundRect()
    {
        var h = Native.GetForegroundWindow();
        if (h == IntPtr.Zero) return null;
        Native.GetWindowThreadProcessId(h, out uint pid);
        if (pid == ownPid) return null;
        if (!Native.IsWindowVisible(h) || Native.IsIconic(h) || Native.IsCloaked(h)) return null;
        if (IgnoredClasses.Contains(Native.ClassName(h))) return null;
        return Native.Bounds(h);
    }

    bool Overlaps(Native.RECT r, double atX, double floor)
    {
        double half = BodyHalfW * S;
        return r.Left < atX + half && r.Right > atX - half && r.Top < floor && r.Bottom > floor - BodyH * S;
    }

    /// <summary>Nearest x where he can stand without being under the given window. Prefers his current screen.</summary>
    double? FindSpot(Native.RECT? fg, Forms.Screen current)
    {
        double half = BodyHalfW * S, margin = 12 * S;
        double? best = null; double bestCost = double.MaxValue;
        foreach (var s in screens)
        {
            var wa = s.WorkingArea;
            var free = new List<(double lo, double hi)> { (wa.Left + half + margin, wa.Right - half - margin) };
            if (fg is { } r && r.Top < wa.Bottom && r.Bottom > wa.Bottom - BodyH * S && r.Right > wa.Left && r.Left < wa.Right)
            {
                double cutLo = r.Left - half - margin, cutHi = r.Right + half + margin;
                free = free.SelectMany(iv => new[] { (iv.lo, Math.Min(iv.hi, cutLo)), (Math.Max(iv.lo, cutHi), iv.hi) })
                           .Where(iv => iv.Item2 > iv.Item1).ToList();
            }
            foreach (var (lo, hi) in free)
            {
                double c = Math.Clamp(x, lo, hi);
                double cost = Math.Abs(c - x) + (s.Equals(current) ? 0 : 400 * S);
                if (cost < bestCost) { bestCost = cost; best = c; }
            }
        }
        return best;
    }

    Forms.Screen ScreenAt(double px)
    {
        foreach (var s in screens)
            if (s.WorkingArea.Left <= px && px < s.WorkingArea.Right) return s;
        return screens.OrderBy(s => Math.Min(Math.Abs(s.WorkingArea.Left - px), Math.Abs(s.WorkingArea.Right - px))).First();
    }

    // ───────────────────────────── behaviours ─────────────────────────────

    void Wander(Forms.Screen scr)
    {
        nextWander = now + 10 + rng.NextDouble() * 25;
        if (rng.NextDouble() < 0.35) { facing = -facing; return; } // just look the other way
        var (lo, hi) = WalkRange(scr);
        if (hi <= lo) return; // perched on something too narrow to stroll along
        double tx = Math.Clamp(x + (rng.NextDouble() * 2 - 1) * 380 * S, lo, hi);
        if (!Perched && ForegroundRect() is { } r && Overlaps(r, tx, scr.WorkingArea.Bottom)) return;
        WalkTo(tx, Store.Config.WalkSpeed);
    }

    void WalkTo(double tx, double spd) { targetX = tx; speed = spd; mode = Mode.Walking; }

    void HopTo(double tx, bool peek)
    {
        mode = Mode.Hopping; hopStart = now; hopToX = tx; hopPeek = peek; hopMoved = false; perchHwnd = IntPtr.Zero;
    }

    void Sleep(bool requested = false) { mode = Mode.Sleeping; napping = requested; Zzz.Visibility = Visibility.Visible; HideBubble(); }

    void Wake(bool quiet = false)
    {
        mode = Mode.Idle; napping = false; Zzz.Visibility = Visibility.Collapsed;
        blinkStart = now; nextWander = now + 6;
        if (!quiet && rng.NextDouble() < 0.3) Say(Pick("I wasn't asleep. I was resting my eyes.", "Oh. You're back.", "Welcome back. Nothing happened. I checked."), 4);
    }

    void Refill()
    {
        Store.RollDay();
        Store.State.Coffees++;
        Store.State.RefilledAt = DateTime.Now;
        Store.SaveState();
        mugMoaned = false;
        squashUntil = now + 0.15;
        int n = Store.State.Coffees;
        Say(n switch
        {
            1 => "First one. The day may now begin.",
            2 => "Number two. Sensible.",
            3 => "Three. Maybe some water next time?",
            4 => "Four. I'm not judging. I'm counting, which is worse.",
            _ => $"That's {n}. Your heart has filed a complaint.",
        }, 4);
    }

    public void SetThinking(bool on) { thinking = on; if (on) HideBubble(); }

    public void Say(string text, double seconds = 6) => ShowBubble(text, seconds, null, null);

    /// <summary>
    /// A nudge: he knocks on the screen, and the bubble stays until a button is pressed (unless the nudge
    /// says otherwise). If it's replaced before being answered, that counts as "ignored".
    /// </summary>
    /// <summary>This nudge is the one in the bubble right now, still waiting for an answer.</summary>
    public bool ShowingNudge(Services.Nudge n) => currentNudge == n && Bubble.Visibility == Visibility.Visible;

    public void ShowNudge(Services.Nudge n)
    {
        if (mode == Mode.Sleeping) Wake(quiet: true);
        act = Act.None; pendingAct = Act.None;
        Zzz.Visibility = Visibility.Collapsed; // e.g. woken from his lunch nap for a meeting
        if (mode == Mode.Walking) mode = Mode.Idle;
        ShowBubble(n.Text, n.Persistent && n.Buttons.Count > 0 ? double.PositiveInfinity : n.Seconds, n.Buttons, n.OnTimeout);
        if (hidden || autoHidden) Services.Pending.Park(n);   // never made it on screen: keep it for the chat's Pending tab
        currentNudge = n; reknocks = 0;
        Knock();
    }

    void ShowBubble(string text, double seconds, List<(string Label, Action Act)>? buttons, Action? onTimeout)
    {
        if (hidden || autoHidden) return;
        // Don't let a casual remark bury an unanswered nudge.
        if (currentNudge != null && Bubble.Visibility == Visibility.Visible && buttons == null) return;
        var replaced = bubbleTimeout; bubbleTimeout = onTimeout;
        if (replaced != null && replaced != onTimeout) replaced();
        ParkNudge();
        BubbleText.Text = text;
        BubbleButtons.Children.Clear();
        foreach (var (label, act) in buttons ?? new())
        {
            var b = new Button
            {
                Content = label, Margin = new Thickness(6, 0, 0, 0), Padding = new Thickness(8, 2, 8, 2),
                Background = Theme.Brush(Theme.Current.Button),
                Foreground = Theme.Brush(Theme.Current.ButtonText), BorderThickness = new Thickness(0), Cursor = Cursors.Hand,
                FontSize = 12,
            };
            b.Click += (_, e) =>
            {
                e.Handled = true; bubbleTimeout = null;
                // Answered: cleared first so hiding the bubble doesn't park it in Pending, and any older copy there goes.
                var answered = currentNudge; currentNudge = null;
                HideBubble();
                if (answered != null) Services.Pending.Answered(answered);
                act();
            };
            BubbleButtons.Children.Add(b);
        }
        BubbleButtons.Visibility = BubbleButtons.Children.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        Bubble.Visibility = Visibility.Visible;
        bubbleUntil = now + seconds;
        bubbleWin?.UpdateLayout(); // resize to the new text now, so it's placed correctly on its first frame
        PlaceBubble(ScreenAt(x));
    }

    /// <summary>Parts drawn in reds that aren't his hoodie (props, mug, eyes): left alone when it's recoloured.</summary>
    static readonly HashSet<string> NotHoodie = new() { "Book", "Sandwich", "Phone", "Mug", "LunchBox", "Eyes", "Ripple" };

    /// <summary>Hoodie colour (and the matching bubble rim and Zzz), plus the speech bubble's light/dark colours.</summary>
    void ApplyAppearance()
    {
        Theme.PaintHoodie(Rig, NotHoodie);
        Theme.PaintHoodie(Zzz, NotHoodie);
        Theme.PaintHoodie(Bubble, NotHoodie);
        var p = Theme.Current;
        Bubble.Background = Theme.Brush(p.Speech);
        BubbleText.Foreground = Theme.Brush(p.Text);
        foreach (var b in BubbleButtons.Children.OfType<Button>())
        {
            b.Background = Theme.Brush(p.Button);
            b.Foreground = Theme.Brush(p.ButtonText);
        }
    }

    /// <summary>Centres the bubble over his head, but keeps it wholly on the monitor he's standing on.</summary>
    void PlaceBubble(Forms.Screen scr)
    {
        if (Bubble.Visibility != Visibility.Visible || bubbleHwnd == IntPtr.Zero) return;
        Native.GetWindowRect(bubbleHwnd, out var r);
        int w = r.Right - r.Left, h = r.Bottom - r.Top;
        var wa = scr.WorkingArea;
        int left = (int)Math.Round(Math.Clamp(x - w / 2.0, wa.Left + 6, Math.Max(wa.Left + 6, wa.Right - w - 6)));
        int top = (int)Math.Round(Math.Max(wa.Top + 6, y - BubbleGap * S - h));
        if (left == r.Left && top == r.Top) return;
        Native.SetWindowPos(bubbleHwnd, Native.HWND_TOPMOST, left, top, 0, 0, Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
    }

    /// <summary>A borderless, click-through-where-empty window that just holds the speech bubble.</summary>
    void MakeBubbleWindow()
    {
        BubbleHolder.Children.Remove(Bubble);
        bubbleWin = new Window
        {
            WindowStyle = WindowStyle.None, AllowsTransparency = true, Background = System.Windows.Media.Brushes.Transparent,
            Topmost = true, ShowInTaskbar = false, ShowActivated = false, ResizeMode = ResizeMode.NoResize,
            SizeToContent = SizeToContent.WidthAndHeight, Content = Bubble, Left = -32000, Top = -32000, Title = "Vex bubble",
        };
        bubbleWin.SourceInitialized += (_, _) =>
        {
            bubbleHwnd = new WindowInteropHelper(bubbleWin).Handle;
            var ex = Native.GetWindowLongPtr(bubbleHwnd, Native.GWL_EXSTYLE);
            Native.SetWindowLongPtr(bubbleHwnd, Native.GWL_EXSTYLE, ex | Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE);
            if (Store.Config.HideFromScreenCapture) Native.SetWindowDisplayAffinity(bubbleHwnd, Native.WDA_EXCLUDEFROMCAPTURE);
        };
        bubbleWin.Show();
    }

    void HideBubble() { ParkNudge(); Bubble.Visibility = Visibility.Collapsed; bubbleUntil = 0; }

    /// <summary>
    /// The nudge in the bubble is going without a button being pressed (replaced, timed out, or hidden because he
    /// dozed off, was dragged or the chat opened): keep it in the chat's Pending tab to answer later.
    /// </summary>
    void ParkNudge()
    {
        if (currentNudge is { } n && Bubble.Visibility == Visibility.Visible) Services.Pending.Park(n);
        currentNudge = null;
    }

    void BubbleExpired()
    {
        var t = bubbleTimeout; bubbleTimeout = null;
        HideBubble();
        t?.Invoke();
    }

    public bool IsHidden => hidden;

    public void ToggleHidden()
    {
        hidden = !hidden;
        Store.Log(hidden ? "Hidden (Hide / Show-hide)" : "Shown again");
        ApplyVisibility();
    }

    /// <summary>Full-screen apps (presentations, video): get out of sight entirely.</summary>
    public void SetAutoHidden(bool on)
    {
        if (on != autoHidden)
            Store.Log(on ? $"Vanished for full screen: {Native.WindowText(Native.GetForegroundWindow())}" : "Back after full screen");
        autoHidden = on;
        if (on) HideBubble();
        ApplyVisibility();
    }

    void ApplyVisibility()
    {
        bool gone = hidden || autoHidden;
        if (gone) HideBubble();
        Opacity = gone ? 0 : 1;
        IsHitTestVisible = !gone;
    }

    public void OpenChat(string? prompt = null)
    {
        InterruptLunch();
        if (hidden) ToggleHidden();
        if (mode == Mode.Sleeping) Wake(quiet: true);
        HideBubble();
        chat ??= new ChatWindow(this);
        chat.ShowNear(x, y - BodyH * S, ScreenAt(x));
        if (prompt != null) chat.Send(prompt);
    }

    /// <summary>
    /// The chat has closed. If his question wasn't answered, drop it, unless a half-typed answer was kept as the draft:
    /// then it's still pending, so the answer sent after reopening is still filed against the right question.
    /// </summary>
    internal void ChatClosed()
    {
        chat = null;
        if (!ChatWindow.HasDraft) Services.Hub.PendingQuestion = null;
    }

    /// <summary>Opens the chat with a question Vex thought of himself; your next message answers it.</summary>
    public void AskInChat(string question)
    {
        OpenChat();
        chat?.Ask(question);
    }

    /// <summary>
    /// QuickChatHotkey (e.g. "Ctrl+Alt+V"): opens the chat from anywhere. Registered on Vex's own window; if another
    /// app already owns the combination it's just logged.
    /// </summary>
    void RegisterQuickChat()
    {
        var spec = Store.Config.QuickChatHotkey;
        if (string.IsNullOrWhiteSpace(spec)) return;
        uint mods = Native.MOD_NOREPEAT;
        Key key = Key.None;
        foreach (var part in spec.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
            switch (part.ToLowerInvariant())
            {
                case "ctrl" or "control": mods |= Native.MOD_CONTROL; break;
                case "alt": mods |= Native.MOD_ALT; break;
                case "shift": mods |= Native.MOD_SHIFT; break;
                case "win": mods |= Native.MOD_WIN; break;
                // Digits are D0-D9 ("1" would otherwise parse as the enum's numeric value); F-keys and letters by name.
                default: key = part.Length == 1 && char.IsDigit(part[0]) ? Key.D0 + (part[0] - '0')
                             : Enum.TryParse(part, true, out Key k) && !int.TryParse(part, out _) ? k : Key.None; break;
            }
        if (key == Key.None || !Native.RegisterHotKey(hwnd, QuickChatId, mods, (uint)KeyInterop.VirtualKeyFromKey(key)))
        { Store.Log($"Quick-chat hotkey \"{spec}\" not available."); return; }
        HwndSource.FromHwnd(hwnd)?.AddHook((IntPtr h, int msg, IntPtr w, IntPtr l, ref bool handled) =>
        {
            if (msg == Native.WM_HOTKEY && w.ToInt32() == QuickChatId) { OpenChat(); handled = true; }
            return IntPtr.Zero;
        });
    }
    const int QuickChatId = 0x5658;

    string Pick(params string[] lines) => lines[rng.Next(lines.Length)];

    void ShowMenu()
    {
        var menu = new ContextMenu();
        void Add(string header, Action a) { var mi = new MenuItem { Header = header }; mi.Click += (_, _) => a(); menu.Items.Add(mi); }
        Add("Chat", () => OpenChat());
        Add("I refilled my coffee", Refill);
        Add(act == Act.Lunch ? "End lunch" : "Lunch break", () => { if (act == Act.Lunch) InterruptLunch(); else StartLunch(); });
        Add("Go to the other screen", () =>
        {
            var other = screens.FirstOrDefault(s => !s.Equals(ScreenAt(x)));
            if (other != null) HopTo(other.WorkingArea.Right - 320 * S, false);
        });
        Add(mode == Mode.Sleeping ? "Wake up" : "Have a nap", () => { if (mode == Mode.Sleeping) Wake(); else Sleep(requested: true); });
        Add("Forget today's chat", () => { chat?.Close(); new ClaudeCodeBrain().Forget(); Say("Forgotten. Mostly.", 3); });
        Add("Hide", ToggleHidden);
        menu.Items.Add(new Separator());
        Add("Quit", () => ((App)Application.Current).Quit());
        menu.IsOpen = true;
    }

    // ───────────────────────────── animation ─────────────────────────────

    /// <summary>
    /// Fine fabric grain: random dark specks, tiled at one texel per unit, laid over the cloth shapes.
    /// Stands in for the SVG's feTurbulence "weave" filter.
    /// </summary>
    static System.Windows.Media.ImageBrush MakeWeave()
    {
        const int n = 96;
        var px = new byte[n * n * 4];
        var r = new Random(2);
        for (int i = 0; i < n * n; i++)
        {
            double a = Math.Max(0, 0.5 * r.NextDouble() - 0.18);
            px[i * 4 + 3] = (byte)(a * 255); // premultiplied black: only alpha
        }
        var bmp = System.Windows.Media.Imaging.BitmapSource.Create(n, n, 96, 96,
            System.Windows.Media.PixelFormats.Pbgra32, null, px, n * 4);
        bmp.Freeze();
        var brush = new System.Windows.Media.ImageBrush(bmp)
        {
            TileMode = System.Windows.Media.TileMode.Tile, Stretch = System.Windows.Media.Stretch.None,
            Viewport = new Rect(0, 0, n, n), ViewportUnits = System.Windows.Media.BrushMappingMode.Absolute,
        };
        brush.Freeze();
        return brush;
    }

    void Animate(double dt)
    {
        Facing.ScaleX = facing;

        // Walk cycle: feet shuffle, body bobs, free glove swings.
        bool walking = mode == Mode.Walking && !hovering;
        double sw = walking ? Math.Sin(walkPhase) : 0;
        LeftFootT.X = sw * 5; LeftFootT.Y = walking ? -Math.Max(0, sw) * 4 : 0;
        RightFootT.X = -sw * 5; RightFootT.Y = walking ? -Math.Max(0, -sw) * 4 : 0;
        LeftGloveT.X = -sw * 4; LeftGloveT.Y = walking ? Math.Abs(sw) * -1.5 : Math.Sin(now * 1.7) * 0.6;

        RightGloveT.Y = MugT.Y = walking ? Math.Abs(Math.Cos(walkPhase)) * -1.5 : 0;

        double breath = Math.Sin(now * (mode == Mode.Sleeping ? 1.2 : 2.0));
        Breath.Y = walking ? -Math.Abs(sw) * 2.5 : breath * (mode == Mode.Sleeping ? 1.5 : 0.8) + (mode == Mode.Sleeping ? 4 : 0);

        // Squash on landing / refill.
        if (now >= squashUntil) squashAmp = 0.08;
        double sq = now < squashUntil ? 1 - squashAmp * Math.Sin((squashUntil - now) / 0.18 * Math.PI) : 1;
        Squash.ScaleY = sq; Squash.ScaleX = 2 - sq;

        // Idle activities (sip, phone, book, sit, stretch, watch) layer on top. See VexActs.cs.
        double eyeCap = 1; double? actTurn = null, actLookY = null;
        ActPose(ref eyeCap, ref actTurn, ref actLookY);
        if (KnockPose()) { actTurn = 0; actLookY = 0; eyeCap = 1; } // face front, eyes wide: knocking on your side of the glass
        RipplePose();
        AirPose(); // flailing when falling, dangling when carried

        // Dangling while dragged.
        Bob.Y = mode == Mode.Dragging ? Math.Sin(now * 9) * 1.5 : 0;

        // Eyes: blink, sleep, squint while thinking, follow the cursor.
        double eyeScale = 1;
        if (mode == Mode.Sleeping) eyeScale = 0.12;
        else
        {
            if (blinkStart < 0 && now >= nextBlink) blinkStart = now;
            if (blinkStart >= 0)
            {
                double b = (now - blinkStart) / 0.14;
                if (b >= 1) { blinkStart = -1; nextBlink = now + (rng.NextDouble() < 0.2 ? 0.25 : 2 + rng.NextDouble() * 4); }
                else eyeScale = Math.Abs(1 - 2 * b) * 0.9 + 0.1;
            }
            if (thinking) eyeScale = Math.Min(eyeScale, 0.45);
            if (mode == Mode.Dragging) eyeScale = Math.Min(eyeScale, 0.6);
            eyeScale = Math.Min(eyeScale, eyeCap);
        }
        Blink.ScaleY = mode == Mode.Falling ? 1.25 : eyeScale;

        // Head turn (design handoff): the face and eyes squash and slide inside the hood, the hood shifts a
        // touch, and the eyes travel a little less than the face so they read as set back. Keep |turn| <= 40.
        double turnTarget = walking ? 25 : 0, ly = 0;
        if (mode is Mode.Idle or Mode.Walking && Native.GetCursorPos(out var c))
        {
            double dx = c.X - x, dy = c.Y - (y - EyeY * S);
            if (Math.Sqrt(dx * dx + dy * dy) < 700 * S)
            {
                turnTarget = Math.Clamp(dx / (300 * S), -1, 1) * 40 * facing;
                ly = Math.Clamp(dy / (250 * S), -1, 1) * 3;
            }
        }
        if (actTurn is { } at) turnTarget = at;
        if (actLookY is { } al) ly = al;
        if (thinking) { turnTarget = Math.Sin(now * 0.9) * 30; ly = -2.5; }
        turn += (turnTarget - turn) * Math.Min(1, dt * 6);
        double th = turn * Math.PI / 180, sn = Math.Sin(th);
        FaceTurn.ScaleX = EyesTurn.ScaleX = Math.Cos(th);
        FaceT.X = 14 * sn;
        Look.X = 11 * sn;
        HoodT.X = 1.5 * sn;
        Look.Y += (ly - Look.Y) * Math.Min(1, dt * 10);

        // Mug drains over time; steam while it's fresh.
        double mins = (DateTime.Now - Store.State.RefilledAt).TotalMinutes;
        double level = Math.Clamp(1 - mins / Store.Config.MugDrainMinutes, 0, 1);
        double h = 14.5 * level;
        Coffee.Height = h;
        Canvas.SetTop(Coffee, 6.25 + 14.5 - h);
        bool steaming = level > 0 && mins < Store.Config.MugSteamMinutes;
        Steam.Opacity += ((steaming ? 0.55 + 0.25 * Math.Sin(now * 2.3) : 0) - Steam.Opacity) * Math.Min(1, dt * 3);
        SteamT.Y = -((now * 6) % 4);
        if (level == 0 && !mugMoaned && Store.State.RefilledAt > DateTime.MinValue && mode == Mode.Idle)
        {
            mugMoaned = true;
            Services.Hub.Nudges.Offer(new Services.Nudge { Type = "mug", Text = "Mug's empty. Just mentioning it.", Seconds = 8, Persistent = false });
        }
    }

    void Place(bool force = false)
    {
        int wx = (int)Math.Round(x - RigCx * S), wy = (int)Math.Round(y - WinH * S);
        bool topmost = now >= nextTopmost;
        if (!force && !topmost && wx == lastWinX && wy == lastWinY) return;
        if (topmost) nextTopmost = now + 2; // other topmost windows can bury us; keep reclaiming the top
        Native.SetWindowPos(hwnd, Native.HWND_TOPMOST, wx, wy, 0, 0, Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
        lastWinX = wx; lastWinY = wy;
    }

    // ───────────────────────────── mouse ─────────────────────────────

    void RigDown(object sender, MouseButtonEventArgs e)
    {
        pressed = true;
        Native.GetCursorPos(out var c);
        pressAt = new Point(c.X, c.Y);
        Rig.CaptureMouse();
        e.Handled = true;
    }

    void RigMove(object sender, MouseEventArgs e)
    {
        if (!pressed || mode == Mode.Dragging) return;
        Native.GetCursorPos(out var c);
        if (Math.Abs(c.X - pressAt.X) + Math.Abs(c.Y - pressAt.Y) < 6 * S) return;
        if (mode == Mode.Sleeping) Wake();
        peeking = false; perchHwnd = IntPtr.Zero; bounces = 0;
        mode = Mode.Dragging; HideBubble();
        grabDx = c.X - x; grabDy = c.Y - y;
        lastCursor = new Point(c.X, c.Y); lastCursorT = now; flingVx = 0;
    }

    void RigUp(object sender, MouseButtonEventArgs e)
    {
        Rig.ReleaseMouseCapture();
        if (!pressed) return;
        pressed = false;
        if (mode == Mode.Dragging)
        {
            mode = Mode.Falling; vy = 0;
            flingVx = Math.Clamp(flingVx, -2500 * S, 2500 * S);
            var floor = ScreenAt(x).WorkingArea.Bottom;
            if (y > floor) y = floor; // dropped onto the taskbar: pop back up
            bounces = 0;
        }
        // Vex's window never takes focus, so the chat doesn't see this click as "elsewhere": clicking him with the chat
        // open next to him dismisses it; if it stays (draft, mid-reply) or was dragged away, this brings it back to him.
        else if (chat == null || chat.Detached || !chat.Dismiss()) OpenChat();
        e.Handled = true;
    }
}
