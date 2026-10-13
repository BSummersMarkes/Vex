using System;
using System.Drawing;
using System.Linq;
using System.Threading;
using System.Windows;
using Forms = System.Windows.Forms;

namespace Vex;

public partial class App : Application
{
    Mutex? single;
    Forms.NotifyIcon? tray;
    VexWindow? vex;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        single = new Mutex(true, @"Local\Vex.Companion", out bool first);
        if (!first) { Shutdown(); return; }

        DispatcherUnhandledException += (_, ex) => { Store.Log($"Unhandled: {ex.Exception}"); ex.Handled = true; };

        // First run: a few questions, so nothing personal is baked into the exe.
        if (!Store.Config.SetupDone && new SetupWindow().ShowDialog() != true) { Shutdown(); return; }

        vex = new VexWindow();
        vex.Show();

        // Just the essentials, same as right-clicking Vex. Everything else lives in Setup
        // (Behaviour → Right now, Privacy → Your data).
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("Setup…", null, (_, _) => OpenSetup());
        menu.Items.Add("What Vex knows…", null, (_, _) => ShowStatus());
        menu.Items.Add(AppearanceMenu(menu));
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Quit", null, (_, _) => Quit());
        tray = new Forms.NotifyIcon { Icon = MakeIcon(), Text = "Vex", Visible = true, ContextMenuStrip = menu };
        tray.MouseClick += (_, me) => { if (me.Button == Forms.MouseButtons.Left) vex.OpenChat(); };
    }

    /// <summary>Tray or right-click → Setup…: the settings window, with Vex's "Right now" actions in it.</summary>
    public void OpenSetup()
    {
        if (vex == null) return;
        if (new SetupWindow(vex).ShowDialog() == true) { Services.Hub.ApplySettings(); vex.Say("Settings saved.", 3, important: true); }
    }

    /// <summary>Tray or right-click → What Vex knows…: a line per source (sitting, notes, calendar, Claude Code, quiet mode) in a bubble.</summary>
    public void ShowStatus() => vex?.Say(Status(), 20, important: true);

    /// <summary>The theme choices, shared by the tray's and Vex's own Appearance menus.</summary>
    static readonly (string Name, string Value)[] Themes = { ("Dark", "dark"), ("Light", "light"), ("Match Windows", "system") };

    /// <summary>Tray → Appearance: dark/light/follow Windows for the chat and bubbles, and the hoodie colour.</summary>
    static Forms.ToolStripMenuItem AppearanceMenu(Forms.ContextMenuStrip menu)
    {
        var root = new Forms.ToolStripMenuItem("Appearance");
        var themes = Themes
            .Select(t => new Forms.ToolStripMenuItem(t.Item1, null, (_, _) => Theme.SetTheme(t.Item2)) { Tag = t.Item2 }).ToList();
        root.DropDownItems.AddRange(themes.ToArray());
        root.DropDownItems.Add(new Forms.ToolStripSeparator());

        var hoodie = new Forms.ToolStripMenuItem("Hoodie colour");
        foreach (var (name, hex) in Theme.Hoodies)
        {
            var c = (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(hex);
            hoodie.DropDownItems.Add(new Forms.ToolStripMenuItem(name, Swatch(c), (_, _) => Theme.SetHoodie(c)) { Tag = hex });
        }
        hoodie.DropDownItems.Add(new Forms.ToolStripSeparator());
        hoodie.DropDownItems.Add("Custom…", null, (_, _) => PickCustomHoodie());
        root.DropDownItems.Add(hoodie);

        // Tick the current choices each time the menu opens.
        menu.Opening += (_, _) =>
        {
            foreach (var t in themes) t.Checked = (string)t.Tag! == Store.Config.Theme;
            var current = Theme.Hex(Theme.Hoodie);
            foreach (var item in hoodie.DropDownItems.OfType<Forms.ToolStripMenuItem>())
                item.Checked = item.Tag is string hex && string.Equals(hex, current, StringComparison.OrdinalIgnoreCase);
        };
        return root;
    }

    /// <summary>
    /// Right-click Vex → Appearance: the same choices as the tray's, as a WPF menu (his own menu isn't a WinForms one).
    /// Built fresh each time his menu opens, so the ticks are always current.
    /// </summary>
    internal static System.Windows.Controls.MenuItem AppearanceMenu()
    {
        static System.Windows.Controls.MenuItem Item(string header, bool ticked, Action a)
        {
            var mi = new System.Windows.Controls.MenuItem { Header = header, IsChecked = ticked };
            mi.Click += (_, _) => a();
            return mi;
        }
        var root = new System.Windows.Controls.MenuItem { Header = "Appearance" };
        foreach (var (name, value) in Themes) root.Items.Add(Item(name, value == Store.Config.Theme, () => Theme.SetTheme(value)));
        root.Items.Add(new System.Windows.Controls.Separator());

        var hoodie = new System.Windows.Controls.MenuItem { Header = "Hoodie colour" };
        var current = Theme.Hex(Theme.Hoodie);
        foreach (var (name, hex) in Theme.Hoodies)
        {
            var c = (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(hex);
            var mi = Item(name, string.Equals(hex, current, StringComparison.OrdinalIgnoreCase), () => Theme.SetHoodie(c));
            mi.Icon = new System.Windows.Shapes.Rectangle { Width = 14, Height = 14, Fill = Theme.Brush(c) };
            hoodie.Items.Add(mi);
        }
        hoodie.Items.Add(new System.Windows.Controls.Separator());
        hoodie.Items.Add(Item("Custom…", false, PickCustomHoodie));
        root.Items.Add(hoodie);
        return root;
    }

    /// <summary>Appearance → Hoodie colour → Custom…: the Windows colour picker, starting from the current colour.</summary>
    static void PickCustomHoodie()
    {
        var cur = Theme.Hoodie;
        using var dlg = new Forms.ColorDialog { FullOpen = true, Color = System.Drawing.Color.FromArgb(cur.R, cur.G, cur.B) };
        if (dlg.ShowDialog() == Forms.DialogResult.OK)
            Theme.SetHoodie(System.Windows.Media.Color.FromRgb(dlg.Color.R, dlg.Color.G, dlg.Color.B));
    }

    /// <summary>A little colour square for the hoodie menu.</summary>
    static Bitmap Swatch(System.Windows.Media.Color c)
    {
        var bmp = new Bitmap(16, 16);
        using var g = Graphics.FromImage(bmp);
        using var b = new SolidBrush(System.Drawing.Color.FromArgb(c.R, c.G, c.B));
        g.FillRectangle(b, 1, 1, 14, 14);
        return bmp;
    }

    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    internal static bool StartsWithWindows()
    {
        using var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(RunKey);
        return k?.GetValue("Vex") is string;
    }

    /// <summary>Per-user startup entry (no admin needed). Points at whichever Vex.exe is running.</summary>
    internal static void SetStartup(bool on)
    {
        using var k = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(RunKey);
        if (on) k.SetValue("Vex", $"\"{Environment.ProcessPath}\"");
        else k.DeleteValue("Vex", throwOnMissingValue: false);
    }

    /// <summary>Opens a file or folder in its usual app. Setup → Privacy → Your data uses it.</summary>
    internal static void Open(string path) =>
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true });

    static string Status()
    {
        var nb = Services.Hub.Notes;
        var cal = Services.Hub.Calendar;
        return string.Join("\n", new[]
        {
            $"Sitting: {(int)Services.Hub.Presence.Sitting.TotalMinutes} min. Coffees: {Store.State.Coffees}.",
            $"Notes: {nb.Name}{(nb.Linked ? $", {nb.OpenChecks.Count + nb.Open.Count()} open to-dos" : " (not set up)")}{(nb.Linked && !nb.Live ? " (app closed)" : "")}.",
            $"Calendar: {(cal.Source == "none" ? "not connected" : $"{cal.Today.Count} today via {cal.Source}")}.",
            $"Claude Code: {(Services.ClaudeWatch.Installed() ? $"{Services.Hub.Claude.Sessions.Count} session(s) seen" : "not connected")}{(Services.Hub.Claude.Listening ? "" : ", listener down")}.",
            $"Quiet mode: {(Services.Hub.InCall != null ? $"in a call ({Services.Hub.InCall})" : Services.Hub.FullScreen ? "full screen" : "off")}.",
        });
    }

    public void Quit()
    {
        Services.Hub.Stop();
        if (tray != null) { tray.Visible = false; tray.Dispose(); }
        Store.SaveState();
        Shutdown();
    }

    /// <summary>A tiny hood with two eyes, drawn at runtime so there's no icon file to maintain.</summary>
    static Icon MakeIcon()
    {
        using var bmp = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.Clear(System.Drawing.Color.Transparent);
            using var hood = new SolidBrush(System.Drawing.Color.FromArgb(0x34, 0x42, 0x6A));
            g.FillEllipse(hood, 1, 1, 30, 30);
            g.FillEllipse(Brushes.Black, 6, 8, 20, 18);
            g.FillEllipse(Brushes.White, 11, 14, 4, 6);
            g.FillEllipse(Brushes.White, 18, 14, 4, 6);
        }
        return Icon.FromHandle(bmp.GetHicon());
    }
}
