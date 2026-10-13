using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using Forms = System.Windows.Forms;

namespace Vex;

public partial class ChatWindow : Window
{
    readonly VexWindow vex;
    readonly IBrain brain = new ClaudeCodeBrain();
    CancellationTokenSource? pending;
    bool detached;
    /// <summary>True once the panel has been dragged away from Vex; clicking him then re-attaches it rather than closing it.</summary>
    public bool Detached => detached;
    /// <summary>What's on screen, in order, for the copy buttons.</summary>
    readonly List<ChatLine> shown = new();
    /// <summary>A half-written message from the last time the chat closed, put back when it reopens. Memory only.</summary>
    static string draft = "";
    /// <summary>Whether a half-written message is being kept for next time (read once the chat has closed).</summary>
    internal static bool HasDraft => !string.IsNullOrWhiteSpace(draft);
    bool closing;
    /// <summary>
    /// Build it / Restart cards still waiting for a click; the chat won't close on a click away while any are, and
    /// closing it anyway moves them to the Pending tab.
    /// </summary>
    readonly List<Services.Confirm> awaitingConfirm = new();
    /// <summary>Showing the Pending tab rather than the conversation.</summary>
    bool showingPending;

    public ChatWindow(VexWindow vex)
    {
        InitializeComponent();
        this.vex = vex;
        Input.Text = draft;
        Input.CaretIndex = Input.Text.Length;
        // Clicking anywhere outside the panel (desktop, another window) takes focus away from it: treat that as a dismiss.
        // Deferred so the close doesn't happen inside the activation change itself.
        Deactivated += (_, _) => Dispatcher.BeginInvoke(() => { if (!IsActive) Dismiss(); });
        Closing += (_, _) => closing = true;
        // Dragging the panel parks it where you put it; clicking Vex again re-attaches it.
        Header.MouseLeftButtonDown += (_, _) => { detached = true; DragMove(); };
        CloseBtn.Click += (_, _) => Close();
        CopyTextBtn.Click += (_, _) => CopyText();
        CopyImageBtn.Click += (_, _) => CopyImage();
        ChatTab.Click += (_, _) => ShowTab(pending: false);
        PendingTab.Click += (_, _) => ShowTab(pending: true);
        Input.KeyDown += InputKey;
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) Close();
            else if (e.Key == Key.C && Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift)) { CopyText(); e.Handled = true; }
        };
        Theme.Changed += Restyle;
        Services.Pending.Changed += RenderPending;
        Closed += (_, _) =>
        {
            draft = Input.Text; Theme.Changed -= Restyle; Services.Pending.Changed -= RenderPending;
            foreach (var c in awaitingConfirm) Services.Pending.Park(c);
            pending?.Cancel(); vex.SetThinking(false); vex.ChatClosed();
        };
        SourceInitialized += (_, _) =>
        {
            if (Store.Config.HideFromScreenCapture)
                Native.SetWindowDisplayAffinity(new WindowInteropHelper(this).Handle, Native.WDA_EXCLUDEFROMCAPTURE);
        };
        Restyle();
        Loaded += (_, _) => Scroll.ScrollToEnd();
    }

    Palette P => Theme.Current;

    /// <summary>Applies the current theme's colours and redraws the messages in them.</summary>
    void Restyle()
    {
        Frame.Background = Theme.Brush(P.Panel);
        Frame.BorderBrush = Theme.Brush(P.PanelBorder);
        TitleText.Foreground = Theme.Brush(P.Text);
        foreach (var b in new[] { CloseBtn, CopyTextBtn, CopyImageBtn }) b.Foreground = Theme.Brush(P.SubText);
        Status.Foreground = Theme.Brush(P.SubText);
        Input.Background = Theme.Brush(P.Input);
        Input.Foreground = Input.CaretBrush = Theme.Brush(P.Text);
        Input.BorderBrush = Theme.Brush(P.InputBorder);
        Badge.Background = Theme.Brush(Theme.Hoodie);
        LoadHistory();
        ShowTab(showingPending);
    }

    // ───────────── pending ─────────────

    /// <summary>Switches between the conversation and the Pending tab.</summary>
    void ShowTab(bool pending)
    {
        showingPending = pending;
        Scroll.Visibility = pending ? Visibility.Collapsed : Visibility.Visible;
        PendingScroll.Visibility = pending ? Visibility.Visible : Visibility.Collapsed;
        ChatTab.Foreground = Theme.Brush(pending ? P.SubText : P.Text);
        PendingTab.Foreground = Theme.Brush(pending ? P.Text : P.SubText);
        RenderPending();
        if (!pending) Scroll.ScrollToEnd();
    }

    /// <summary>Redraws the Pending list and the count on its tab. Raised by <see cref="Services.Pending.Changed"/> too.</summary>
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
            // Its original options, plus a plain ✕ to clear it if it didn't come with one.
            var options = item.Options.Contains(Services.Pending.Drop) ? item.Options : item.Options.Append(Services.Pending.Drop);
            var buttons = new WrapPanel();
            foreach (var label in options)
            {
                var b = CardButton(label);
                b.Click += (_, _) => ChoosePending(item, label);
                buttons.Children.Add(b);
            }
            var when = item.At.Date == DateTime.Today ? item.At.ToString("HH:mm") : item.At.ToString("ddd d MMM, HH:mm");
            PendingList.Children.Add(Card(new StackPanel
            {
                Children =
                {
                    new TextBlock { Text = when, Foreground = Theme.Brush(P.SubText), FontSize = 11 },
                    new TextBlock { Text = item.Text, TextWrapping = TextWrapping.Wrap, Foreground = Theme.Brush(P.Text), FontSize = 13 },
                    buttons,
                },
            }));
        }
    }

    /// <summary>
    /// An option picked from Pending: it does what the button would have done (which may start a chat, e.g. "Prep me";
    /// if so, the conversation comes back into view). Not while he's mid-reply, since a prompt sent then would be lost.
    /// </summary>
    void ChoosePending(Services.PendingItem item, string option)
    {
        if (pending != null) return;
        int before = Messages.Children.Count;
        var note = Services.Pending.Choose(item.Id, option);
        if (note != null) Add("note", note);
        if (Messages.Children.Count != before && showingPending) ShowTab(pending: false);
    }

    /// <summary>Pick up where we left off; a fresh "What." only when there's no history.</summary>
    void LoadHistory()
    {
        Messages.Children.Clear();
        shown.Clear();
        var history = ChatLog.Recent();
        DateTime? last = null;
        foreach (var line in history)
        {
            if (last == null || line.At.Date != last.Value.Date) Divider(line.At.Date == DateTime.Today ? "Today" : line.At.ToString("dddd d MMMM"));
            Render(line.Who, line.Text, line.At);
            last = line.At;
        }
        if (history.Count == 0) Render("vex", "What.", DateTime.Now);
    }

    // ───────────── copying ─────────────

    /// <summary>The visible chat as plain text, one message per paragraph, for pasting into email or Teams.</summary>
    void CopyText()
    {
        var me = Store.Config.UserName.Length > 0 ? Store.Config.UserName : "Me";
        var text = string.Join(Environment.NewLine + Environment.NewLine, shown.Select(l => l.Who switch
        {
            "note" => $"    {l.Text}",
            "me" => $"[{l.At:HH:mm}] {me}: {l.Text}",
            _ => $"[{l.At:HH:mm}] Vex: {l.Text}",
        }));
        try { Clipboard.SetText(text); Flash("Copied the chat as text."); }
        catch (Exception ex) { Store.Log($"Copy text: {ex.Message}"); Flash("Couldn't get at the clipboard."); }
    }

    /// <summary>
    /// The whole visible chat (not just the scrolled part) as a picture on the clipboard, on the panel's background.
    /// Rendered directly, so it works even though Vex's windows are hidden from screenshots.
    /// </summary>
    void CopyImage()
    {
        try
        {
            const double pad = 12;
            double w = Messages.ActualWidth, h = Messages.ActualHeight, dpi = 96 * VisualTreeHelper.GetDpi(this).DpiScaleX;
            var dv = new DrawingVisual();
            using (var dc = dv.RenderOpen())
            {
                dc.DrawRectangle(Theme.Brush(Color.FromRgb(P.Panel.R, P.Panel.G, P.Panel.B)), null, new Rect(0, 0, w + 2 * pad, h + 2 * pad));
                dc.DrawRectangle(new VisualBrush(Messages) { Stretch = Stretch.None, AlignmentX = AlignmentX.Left, AlignmentY = AlignmentY.Top },
                                 null, new Rect(pad, pad, w, h));
            }
            var bmp = new System.Windows.Media.Imaging.RenderTargetBitmap(
                (int)Math.Ceiling((w + 2 * pad) * dpi / 96), (int)Math.Ceiling((h + 2 * pad) * dpi / 96), dpi, dpi, PixelFormats.Pbgra32);
            bmp.Render(dv);
            Clipboard.SetImage(bmp);
            Flash("Copied the chat as a picture.");
        }
        catch (Exception ex) { Store.Log($"Copy image: {ex.Message}"); Flash("Couldn't copy that."); }
    }

    /// <summary>A short note in the status line, unless Vex is busy thinking.</summary>
    async void Flash(string text)
    {
        if (pending != null) return;
        Status.Text = text;
        Status.Visibility = Visibility.Visible;
        await System.Threading.Tasks.Task.Delay(1800);
        if (pending != null) return;
        Status.Visibility = Visibility.Collapsed;
        Status.Text = "Vex is thinking…";
    }

    /// <summary>Shows a message and keeps it in the transcript on disk.</summary>
    void Add(string who, string text)
    {
        Render(who, text, DateTime.Now);
        ChatLog.Append(who, text);
    }

    void Divider(string label) => Messages.Children.Add(new TextBlock
    {
        Text = label, Foreground = Theme.Brush(P.Divider), FontSize = 11,
        HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 8, 0, 4),
    });

    /// <summary>
    /// Closes the panel after a click elsewhere, the same as ✕. A half-typed message is safe (it's kept in
    /// <see cref="draft"/>) and the conversation is on disk, but it stays open while Vex is mid-reply, while a
    /// Build it / Restart card is waiting (closing moves those to Pending), or while a modal dialog is up, since none of those
    /// should be thrown away by a stray click. Returns whether it closed.
    /// </summary>
    public bool Dismiss()
    {
        if (closing || !IsVisible || !Store.Config.ChatClosesOnClickAway) return false;
        if (pending != null || awaitingConfirm.Count > 0) return false;
        // A dialog shown with ShowDialog (ours or a WinForms one) takes focus from the chat; that isn't a click away.
        if (ComponentDispatcher.IsThreadModal || OwnedWindows.Cast<Window>().Any(w => w.IsVisible)) return false;
        Close();
        return true;
    }

    /// <summary>Pops up above (and clear of) Vex, kept on the same screen. Coordinates are physical pixels.</summary>
    public void ShowNear(double feetX, double headY, Forms.Screen scr)
    {
        if (!IsVisible) Show();
        detached = false;
        Follow(feetX, headY, scr);
        Activate();
        Input.Focus();
    }

    /// <summary>Called every frame Vex moves, in the same step, so the panel travels with him.</summary>
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

    void InputKey(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)) return;
        e.Handled = true;
        var text = Input.Text.Trim();
        if (text.Length == 0 || pending != null) return;
        Input.Text = "";
        Send(text);
    }

    /// <summary>Sends a message to Vex as if typed, shows the reply and carries out any actions in it.</summary>
    public async void Send(string text)
    {
        if (pending != null) return;
        if (showingPending) ShowTab(pending: false);   // the reply should be in view
        Add("me", text);
        pending = new CancellationTokenSource();
        Status.Text = "Vex is thinking…";
        Status.Visibility = Visibility.Visible;
        vex.SetThinking(true);
        try
        {
            var raw = await brain.AskAsync(text, pending.Token);
            var (reply, done, confirms) = Services.Hub.RunActions(raw);
            Add("vex", reply.Length > 0 ? reply : "...");
            foreach (var d in done) Add("note", d);
            foreach (var c in confirms) ConfirmCard(c);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Store.Log($"Chat failed: {ex}");
            Add("vex", ex is InvalidOperationException ? ex.Message : "Something broke. It's in the log.");
        }
        finally
        {
            pending = null;
            Status.Visibility = Visibility.Collapsed;
            vex.SetThinking(false);
        }
    }

    /// <summary>
    /// Vex asking something of his own accord (a <see cref="Services.Curiosity"/> question): shown as his message, and
    /// passed to the brain with the next message so it knows what the answer is about.
    /// </summary>
    public void Ask(string question)
    {
        if (showingPending) ShowTab(pending: false);
        Add("vex", question);
        Services.Hub.PendingQuestion = question;
        Input.Focus();
    }

    /// <summary>
    /// An action that changes Vex himself (build, restart), waiting for a click. Only a person at the keyboard can press
    /// it, so text planted in a window title or meeting invite can't get a build started on its own. Not kept on disk:
    /// close the chat and it moves to the Pending tab (in state.json), still needing that click.
    /// </summary>
    void ConfirmCard(Services.Confirm c)
    {
        var go = CardButton(c.GoLabel);
        var no = CardButton("No");
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Children = { go, no } };
        var card = Card(new StackPanel { Children = { new TextBlock { Text = c.Text, TextWrapping = TextWrapping.Wrap, Foreground = Theme.Brush(P.Text), FontSize = 13 }, buttons } });
        awaitingConfirm.Add(c);
        go.Click += (_, _) => { buttons.IsEnabled = false; awaitingConfirm.Remove(c); Add("note", c.Go()); };
        no.Click += (_, _) => { buttons.IsEnabled = false; awaitingConfirm.Remove(c); Add("note", "✗ Left it."); };
        Messages.Children.Add(card);
        Scroll.ScrollToEnd();
    }

    /// <summary>The outlined card that Build it / Restart prompts and Pending items sit in.</summary>
    Border Card(UIElement content) => new()
    {
        Background = Theme.Brush(P.VexBubble), BorderBrush = Theme.Brush(P.Note), BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(10), Padding = new Thickness(10, 6, 10, 8), Margin = new Thickness(0, 3, 40, 3),
        HorizontalAlignment = HorizontalAlignment.Left, Child = content,
    };

    Button CardButton(string label) => new()
    {
        Content = label, Padding = new Thickness(10, 3, 10, 3), Margin = new Thickness(0, 6, 6, 0), Cursor = Cursors.Hand,
        Background = Theme.Brush(P.Button), Foreground = Theme.Brush(P.ButtonText), BorderThickness = new Thickness(0),
    };

    void Render(string who, string text, DateTime at)
    {
        shown.Add(new ChatLine(at, who, text));
        if (who == "note")
        {
            Messages.Children.Add(new TextBlock
            {
                Text = text, Foreground = Theme.Brush(P.Note), FontSize = 11.5,
                Margin = new Thickness(4, 1, 0, 3), TextWrapping = TextWrapping.Wrap,
            });
            Scroll.ScrollToEnd();
            return;
        }
        bool me = who == "me";
        var bubble = new Border
        {
            Background = Theme.Brush(me ? P.MeBubble : P.VexBubble),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(10, 6, 10, 6),
            Margin = new Thickness(me ? 40 : 0, 3, me ? 0 : 40, 3),
            HorizontalAlignment = me ? HorizontalAlignment.Right : HorizontalAlignment.Left,
            Child = new TextBox
            {
                Text = text, IsReadOnly = true, TextWrapping = TextWrapping.Wrap, BorderThickness = new Thickness(0),
                Background = Brushes.Transparent, Foreground = Theme.Brush(P.Text),
                FontSize = 13,
            },
        };
        Messages.Children.Add(bubble);
        Scroll.ScrollToEnd();
    }
}
