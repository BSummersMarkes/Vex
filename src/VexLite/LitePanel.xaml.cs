using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using Forms = System.Windows.Forms;

namespace Vex;

/// <summary>
/// The lite build's panel, in place of the chat (VexWindow uses it under the name ChatWindow, so the shared code that
/// opens, follows and dismisses the chat works unchanged). Clicking Vex opens it: set a reminder in plain words
/// ("20m", "daily 10:00", read by Services.ReminderTimes, no AI), see what's coming and cancel any, answer nudges
/// that went unanswered (Pending), and the few "right now" things: break, lunch, coffee.
/// </summary>
public partial class LitePanel : Window
{
    readonly VexWindow vex;
    bool detached, closing, showingPending;
    /// <summary>True once the panel has been dragged away from Vex; clicking him then re-attaches it rather than closing it.</summary>
    public bool Detached => detached;
    /// <summary>A half-typed reminder from the last time the panel closed, put back when it reopens. Memory only.</summary>
    static string draftWhen = "", draftWhat = "";
    internal static bool HasDraft => draftWhen.Trim().Length > 0 || draftWhat.Trim().Length > 0;

    Palette P => Theme.Current;

    public LitePanel(VexWindow vex)
    {
        InitializeComponent();
        this.vex = vex;
        When.Text = draftWhen; What.Text = draftWhat;
        Deactivated += (_, _) => Dispatcher.BeginInvoke(new Action(() => { if (!IsActive) Dismiss(); }));
        Closing += (_, _) => closing = true;
        Header.MouseLeftButtonDown += (_, _) => { detached = true; DragMove(); };
        CloseBtn.Click += (_, _) => Close();
        RemindTab.Click += (_, _) => ShowTab(pending: false);
        PendingTab.Click += (_, _) => ShowTab(pending: true);
        AddBtn.Click += (_, _) => AddReminder();
        foreach (var box in new[] { When, What })
        {
            box.TextChanged += (_, _) => Hints();
            box.KeyDown += (_, e) => { if (e.Key == Key.Enter) { e.Handled = true; AddReminder(); } };
        }
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) Close(); };
        Theme.Changed += Restyle;
        Services.Pending.Changed += RenderPending;
        Closed += (_, _) =>
        {
            draftWhen = When.Text; draftWhat = What.Text;
            Theme.Changed -= Restyle; Services.Pending.Changed -= RenderPending;
            vex.ChatClosed();
        };
        SourceInitialized += (_, _) =>
        {
            if (Store.Config.HideFromScreenCapture)
                Native.SetWindowDisplayAffinity(new WindowInteropHelper(this).Handle, Native.WDA_EXCLUDEFROMCAPTURE);
        };
        Restyle();
    }

    void Restyle()
    {
        Frame.Background = Theme.Brush(P.Panel);
        Frame.BorderBrush = Theme.Brush(P.PanelBorder);
        TitleText.Foreground = Theme.Brush(P.Text);
        CloseBtn.Foreground = Theme.Brush(P.SubText);
        foreach (var t in new[] { AddTitle, UpcomingTitle, NowTitle }) t.Foreground = Theme.Brush(P.Text);
        foreach (var t in new[] { Help, WhenHint, WhatHint }) t.Foreground = Theme.Brush(P.SubText);
        foreach (var box in new[] { When, What })
        {
            box.Background = Theme.Brush(P.Input);
            box.Foreground = box.CaretBrush = Theme.Brush(P.Text);
            box.BorderBrush = Theme.Brush(P.InputBorder);
        }
        AddBtn.Background = Theme.Brush(P.Button);
        AddBtn.Foreground = Theme.Brush(P.ButtonText);
        Badge.Background = Theme.Brush(Theme.Hoodie);
        Hints();
        RenderUpcoming();
        RenderNow();
        ShowTab(showingPending);
    }

    void Hints()
    {
        WhenHint.Visibility = When.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        WhatHint.Visibility = What.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    void ShowTab(bool pending)
    {
        showingPending = pending;
        RemindPage.Visibility = pending ? Visibility.Collapsed : Visibility.Visible;
        PendingPage.Visibility = pending ? Visibility.Visible : Visibility.Collapsed;
        RemindTab.Foreground = Theme.Brush(pending ? P.SubText : P.Text);
        PendingTab.Foreground = Theme.Brush(pending ? P.Text : P.SubText);
        RenderPending();
    }

    // ───────────── reminders ─────────────

    void AddReminder()
    {
        var what = What.Text.Trim();
        if (When.Text.Trim().Length == 0) { When.Focus(); return; }
        if (what.Length == 0) { What.Focus(); return; }
        if (!Services.ReminderTimes.TryParse(When.Text, DateTime.Now, out var at, out var repeat))
        {
            Help.Text = $"✗ I can't read \"{When.Text.Trim()}\" as a time. Try 20m, 14:30, tomorrow 9:00 or daily 10:00.";
            Help.Foreground = Theme.Brush(P.Text);
            When.Focus(); When.SelectAll();
            return;
        }
        var r = new Reminder { At = at, Text = what, Repeat = repeat };
        Store.State.Reminders.Add(r);
        Store.SaveState();
        When.Text = What.Text = "";
        Help.Text = $"✓ {Services.ReminderTimes.Describe(r, DateTime.Now)}: {r.Text}";
        Help.Foreground = Theme.Brush(P.SubText);
        RenderUpcoming();
        When.Focus();
    }

    void RenderUpcoming()
    {
        Upcoming.Children.Clear();
        var list = Store.State.Reminders.OrderBy(r => r.At).ToList();
        if (list.Count == 0)
        {
            Upcoming.Children.Add(new TextBlock { Text = "Nothing yet.", Foreground = Theme.Brush(P.Divider), FontSize = 12, Margin = new Thickness(2, 2, 0, 2) });
            return;
        }
        foreach (var r in list)
        {
            var remove = new Button
            {
                Content = "✕", Width = 22, Height = 22, Background = System.Windows.Media.Brushes.Transparent, BorderThickness = new Thickness(0),
                Foreground = Theme.Brush(P.SubText), Cursor = Cursors.Hand, ToolTip = r.Repeat.Length > 0 ? "Stop these" : "Cancel it",
                VerticalAlignment = VerticalAlignment.Top,
            };
            remove.Click += (_, _) => { Store.State.Reminders.Remove(r); Store.SaveState(); RenderUpcoming(); };
            var row = new DockPanel { Margin = new Thickness(0, 2, 0, 2) };
            DockPanel.SetDock(remove, Dock.Right);
            row.Children.Add(remove);
            row.Children.Add(new StackPanel
            {
                Children =
                {
                    new TextBlock { Text = Services.ReminderTimes.Describe(r, DateTime.Now), Foreground = Theme.Brush(P.SubText), FontSize = 11 },
                    new TextBlock { Text = r.Text, Foreground = Theme.Brush(P.Text), FontSize = 13, TextWrapping = TextWrapping.Wrap },
                },
            });
            Upcoming.Children.Add(new Border
            {
                Background = Theme.Brush(P.VexBubble), BorderBrush = Theme.Brush(P.Note), BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8), Padding = new Thickness(8, 4, 4, 5), Margin = new Thickness(0, 2, 0, 2), Child = row,
            });
        }
    }

    // ───────────── right now ─────────────

    void RenderNow()
    {
        NowButtons.Children.Clear();
        void Add(string label, Action act)
        {
            var b = CardButton(label);
            b.Click += (_, _) => { act(); RenderNow(); };
            NowButtons.Children.Add(b);
        }
        Add("Had a break", () => { Services.Hub.Presence.ConfirmBreak(lunch: false); vex.Say("Noted. Sitting timer reset.", 3, important: true); });
        if (vex.OnLunch) Add("Back from lunch", () => { Close(); vex.LunchBack(); });
        else Add("Going to lunch", () => { Services.Hub.Presence.ConfirmBreak(lunch: true); Close(); vex.StartLunch(); });
        Add("Refilled my coffee", () => vex.RefillCoffee());
    }

    // ───────────── pending ─────────────

    void RenderPending()
    {
        var items = Services.Pending.All;
        BadgeText.Text = items.Count.ToString();
        Badge.Visibility = items.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        PendingList.Children.Clear();
        if (items.Count == 0)
        {
            PendingList.Children.Add(new TextBlock
            {
                Text = "Nothing waiting on you.", Foreground = Theme.Brush(P.Divider), FontSize = 11,
                HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 8, 0, 4),
            });
            return;
        }
        foreach (var item in items)
        {
            var options = item.Options.Contains(Services.Pending.Drop) ? item.Options : item.Options.Concat(new[] { Services.Pending.Drop }).ToList();
            var buttons = new WrapPanel();
            foreach (var label in options)
            {
                var b = CardButton(label);
                b.Click += (_, _) => Services.Pending.Choose(item.Id, label);
                buttons.Children.Add(b);
            }
            var when = item.At.Date == DateTime.Today ? item.At.ToString("HH:mm") : item.At.ToString("ddd d MMM, HH:mm");
            PendingList.Children.Add(new Border
            {
                Background = Theme.Brush(P.VexBubble), BorderBrush = Theme.Brush(P.Note), BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(10), Padding = new Thickness(10, 6, 10, 8), Margin = new Thickness(0, 3, 0, 3),
                Child = new StackPanel
                {
                    Children =
                    {
                        new TextBlock { Text = when, Foreground = Theme.Brush(P.SubText), FontSize = 11 },
                        new TextBlock { Text = item.Text, TextWrapping = TextWrapping.Wrap, Foreground = Theme.Brush(P.Text), FontSize = 13 },
                        buttons,
                    },
                },
            });
        }
    }

    Button CardButton(string label) => new()
    {
        Content = label, Padding = new Thickness(10, 3, 10, 3), Margin = new Thickness(0, 6, 6, 0), Cursor = Cursors.Hand,
        Background = Theme.Brush(P.Button), Foreground = Theme.Brush(P.ButtonText), BorderThickness = new Thickness(0),
    };

    // ───────────── what VexWindow calls (the same as the chat's) ─────────────

    /// <summary>Closes on a click away, unless a half-typed reminder or a dialog is in the way. True if it closed.</summary>
    public bool Dismiss()
    {
        if (closing || !IsVisible || !Store.Config.ChatClosesOnClickAway) return false;
        if (ComponentDispatcher.IsThreadModal || OwnedWindows.Cast<Window>().Any(w => w.IsVisible)) return false;
        Close();
        return true;
    }

    /// <summary>Pops up above (and clear of) Vex, kept on the same screen. Coordinates are physical pixels.</summary>
    public void ShowNear(double feetX, double headY, Forms.Screen scr)
    {
        if (!IsVisible) Show();
        detached = false;
        UpdateLayout();
        Follow(feetX, headY, scr);
        Activate();
        if (!showingPending) When.Focus();
    }

    /// <summary>Called every frame Vex moves, so the panel travels with him.</summary>
    public void Follow(double feetX, double headY, Forms.Screen scr)
    {
        if (detached) return;
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return;
        Native.GetWindowRect(hwnd, out var r);
        int w = r.Right - r.Left, h = r.Bottom - r.Top;
        var wa = scr.WorkingArea;
        int left = (int)Math.Round(Math.Clamp(feetX - w / 2.0, wa.Left + 8, Math.Max(wa.Left + 8, wa.Right - w - 8)));
        int top = (int)Math.Round(Math.Clamp(headY - h - 8, wa.Top + 8, Math.Max(wa.Top + 8, wa.Bottom - h - 8)));
        if (left == r.Left && top == r.Top) return;
        Native.SetWindowPos(hwnd, Native.HWND_TOPMOST, left, top, 0, 0, Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
    }

    /// <summary>The chat's "send this prompt" and "ask this": there's no one to ask in the lite build, so the panel just opens.</summary>
    public void Send(string prompt) { }
    public void Ask(string question) { }
}
