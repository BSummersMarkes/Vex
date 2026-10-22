using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using Vex.Services;

namespace Vex;

/// <summary>
/// Right-click → To-do list: today's journal checkboxes as a small checklist, entirely local (no Claude). Ticking a box
/// and adding an item go through the same <see cref="INotes"/> calls as the chat's [[done]] and [[todo]] actions, so the
/// journal stays the one real list (and the notebook's To-Do mirror follows it as usual).
/// </summary>
public partial class TodoWindow : Window
{
    /// <summary>The open one, so the menu brings it forward instead of stacking a second copy.</summary>
    static TodoWindow? open;

    /// <summary>
    /// Ticks and adds sent but not yet visible in the journal (the Lab Notebook applies them when it next reads its inbox,
    /// which may be much later if it's closed). Shown as done/added meanwhile so a click doesn't look ignored. Memory only.
    /// </summary>
    static readonly HashSet<string> ticking = new();
    static readonly List<string> adding = new();

    /// <summary>Re-reads the notes' copy of today every second; cheap, and catches edits made in the notebook or an editor.</summary>
    readonly DispatcherTimer poll;
    string shownJournal = "\0";

    /// <summary>Opens the checklist, or brings the open one to the front.</summary>
    public static void ShowList()
    {
        if (open != null) { open.Activate(); return; }
        open = new TodoWindow();
        open.Show();
        open.Activate();
        open.Input.Focus();
    }

    TodoWindow()
    {
        InitializeComponent();
        Header.MouseLeftButtonDown += (_, _) => DragMove();
        CloseBtn.Click += (_, _) => Close();
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) Close(); };
        Input.KeyDown += (_, e) => { if (e.Key == Key.Enter) { Add(); e.Handled = true; } };
        poll = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, (_, _) => Render(), Dispatcher);
        Theme.Changed += Restyle;
        Closed += (_, _) => { poll.Stop(); Theme.Changed -= Restyle; open = null; };
        SourceInitialized += (_, _) =>
        {
            if (Store.Config.HideFromScreenCapture)
                Native.SetWindowDisplayAffinity(new WindowInteropHelper(this).Handle, Native.WDA_EXCLUDEFROMCAPTURE);
        };
        Restyle();
        poll.Start();
    }

    Palette P => Theme.Current;

    void Restyle()
    {
        Frame.Background = Theme.Brush(P.Panel);
        Frame.BorderBrush = Theme.Brush(P.PanelBorder);
        TitleText.Foreground = Theme.Brush(P.Text);
        CloseBtn.Foreground = Theme.Brush(P.SubText);
        Status.Foreground = Theme.Brush(P.SubText);
        Input.Background = Theme.Brush(P.Input);
        Input.Foreground = Input.CaretBrush = Theme.Brush(P.Text);
        Input.BorderBrush = Theme.Brush(P.InputBorder);
        shownJournal = "\0";   // force a redraw in the new colours
        Render();
    }

    static string Norm(string s) => Regex.Replace(s.Trim(), @"\s+", " ").ToLowerInvariant();

    /// <summary>Every checkbox in today's journal, ticked or not, skipping fenced code blocks (same reading as the notes stores).</summary>
    static List<(string Text, bool Done)> Today(string body)
    {
        var items = new List<(string, bool)>();
        bool fenced = false;
        foreach (var raw in body.Split('\n'))
        {
            var line = raw.TrimEnd((char)13);
            if (line.TrimStart().StartsWith("```")) { fenced = !fenced; continue; }
            if (!fenced && INotes.AnyCheckRx.Match(line) is { Success: true } m)
                items.Add((m.Groups["text"].Value.Trim(), m.Groups["mark"].Value != " "));
        }
        return items;
    }

    /// <summary>Redraws the list when today's journal (or what's waiting to reach it) has changed.</summary>
    void Render()
    {
        Store.RollDay();
        var notes = Hub.Notes;
        var body = notes.Linked ? notes.TodayJournal : "";
        var key = body + "\u0001" + string.Join("\u0001", ticking) + "\u0001" + string.Join("\u0001", adding);
        if (key == shownJournal) return;
        shownJournal = key;

        var items = Today(body);
        // Once the journal shows a tick or an added line, it's no longer waiting.
        ticking.RemoveWhere(t => items.Any(i => i.Done && Norm(i.Text) == t));
        adding.RemoveAll(a => items.Any(i => Norm(i.Text) == Norm(a)));
        var rows = items.Select(i => (i.Text, Done: i.Done || ticking.Contains(Norm(i.Text)), Waiting: !i.Done && ticking.Contains(Norm(i.Text))))
                        .Concat(adding.Select(a => (Text: a, Done: false, Waiting: true)))
                        .ToList();

        Items.Children.Clear();
        foreach (var (text, done, waiting) in rows)
        {
            var box = new CheckBox
            {
                Content = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, TextDecorations = done ? TextDecorations.Strikethrough : null },
                IsChecked = done, Margin = new Thickness(2, 3, 2, 3), FontSize = 13, Cursor = Cursors.Hand,
                Foreground = Theme.Brush(done ? P.SubText : P.Text),
                // The inbox has no "untick", so a ticked box stays ticked here; untick it in the journal itself.
                IsEnabled = !done,
                ToolTip = waiting ? "Waiting for the notebook to pick this up" : done ? "Done (untick it in the journal if that was a mistake)" : "Tick it off",
            };
            if (!done) box.Checked += (_, _) => Tick(text);
            Items.Children.Add(box);
        }

        string? status = !notes.Linked ? "No notes set up yet: pick a notes location in Setup → Notes."
            : rows.Count == 0 ? "Nothing on today's list. Type below to add something."
            : !notes.Live && (ticking.Count > 0 || adding.Count > 0) ? $"{notes.Name} is closed: changes go in when it next opens."
            : null;
        Status.Text = status ?? "";
        Status.Visibility = status == null ? Visibility.Collapsed : Visibility.Visible;
        Input.IsEnabled = notes.Linked;
    }

    /// <summary>Ticks the checkbox in the journal, as [[done]] would.</summary>
    void Tick(string text)
    {
        if (Hub.Notes.JournalDone(text) != null) { ticking.Add(Norm(text)); Render(); return; }
        shownJournal = "\0";   // redraw to put the box back to unticked
        Render();
        Status.Text = $"Couldn't reach {Hub.Notes.Name}.";
        Status.Visibility = Visibility.Visible;
    }

    /// <summary>Adds the typed line to today's journal as "- [ ] …", as [[todo]] would.</summary>
    void Add()
    {
        var text = Regex.Replace(Input.Text, @"\s+", " ").Trim();
        if (text.Length == 0) return;
        if (Hub.Notes.JournalTodo(text) == null) { Status.Text = $"Couldn't reach {Hub.Notes.Name}."; Status.Visibility = Visibility.Visible; return; }
        Input.Clear();
        // Local notes write the file straight away; the notebook only once it reads the inbox.
        if (!Today(Hub.Notes.TodayJournal).Any(i => Norm(i.Text) == Norm(text))) adding.Add(text);
        Render();
        Scroll.ScrollToEnd();
    }
}
