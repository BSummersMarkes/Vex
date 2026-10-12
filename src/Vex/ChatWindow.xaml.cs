using System;
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

    public ChatWindow(VexWindow vex)
    {
        InitializeComponent();
        this.vex = vex;
        // Dragging the panel parks it where you put it; clicking Vex again re-attaches it.
        Header.MouseLeftButtonDown += (_, _) => { detached = true; DragMove(); };
        CloseBtn.Click += (_, _) => Close();
        Input.KeyDown += InputKey;
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) Close(); };
        Closed += (_, _) => { pending?.Cancel(); vex.SetThinking(false); vex.ChatClosed(); };
        SourceInitialized += (_, _) =>
        {
            if (Store.Config.HideFromScreenCapture)
                Native.SetWindowDisplayAffinity(new WindowInteropHelper(this).Handle, Native.WDA_EXCLUDEFROMCAPTURE);
        };
        // Pick up where we left off; a fresh "What." only when there's no history.
        var history = ChatLog.Recent();
        DateTime? last = null;
        foreach (var line in history)
        {
            if (last == null || line.At.Date != last.Value.Date) Divider(line.At.Date == DateTime.Today ? "Today" : line.At.ToString("dddd d MMMM"));
            Render(line.Who, line.Text);
            last = line.At;
        }
        if (history.Count == 0) Render("vex", "What.");
        Loaded += (_, _) => Scroll.ScrollToEnd();
    }

    /// <summary>Shows a message and keeps it in the transcript on disk.</summary>
    void Add(string who, string text)
    {
        Render(who, text);
        ChatLog.Append(who, text);
    }

    void Divider(string label) => Messages.Children.Add(new TextBlock
    {
        Text = label, Foreground = new SolidColorBrush(Color.FromRgb(0x6F, 0x77, 0x92)), FontSize = 11,
        HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 8, 0, 4),
    });

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
        Add("me", text);
        pending = new CancellationTokenSource();
        Status.Visibility = Visibility.Visible;
        vex.SetThinking(true);
        try
        {
            var raw = await brain.AskAsync(text, pending.Token);
            var (reply, done) = Services.Hub.RunActions(raw);
            Add("vex", reply.Length > 0 ? reply : "...");
            foreach (var d in done) Add("note", d);
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

    void Render(string who, string text)
    {
        if (who == "note")
        {
            Messages.Children.Add(new TextBlock
            {
                Text = text, Foreground = new SolidColorBrush(Color.FromRgb(0x8C, 0xC4, 0x9A)), FontSize = 11.5,
                Margin = new Thickness(4, 1, 0, 3), TextWrapping = TextWrapping.Wrap,
            });
            Scroll.ScrollToEnd();
            return;
        }
        bool me = who == "me";
        var bubble = new Border
        {
            Background = new SolidColorBrush(me ? Color.FromRgb(0x34, 0x42, 0x6A) : Color.FromRgb(0x23, 0x28, 0x42)),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(10, 6, 10, 6),
            Margin = new Thickness(me ? 40 : 0, 3, me ? 0 : 40, 3),
            HorizontalAlignment = me ? HorizontalAlignment.Right : HorizontalAlignment.Left,
            Child = new TextBox
            {
                Text = text, IsReadOnly = true, TextWrapping = TextWrapping.Wrap, BorderThickness = new Thickness(0),
                Background = Brushes.Transparent, Foreground = new SolidColorBrush(Color.FromRgb(0xE8, 0xEA, 0xF2)),
                FontSize = 13,
            },
        };
        Messages.Children.Add(bubble);
        Scroll.ScrollToEnd();
    }
}
