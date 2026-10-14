using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows;

namespace Vex;

/// <summary>
/// First-run setup (and tray → Setup… afterwards). Everything personal lives in config.json, so the
/// same Vex.exe can be handed to anyone.
/// </summary>
public partial class SetupWindow : Window
{
#if !LITE
    readonly string? notebookPath = Services.Notebook.Detect();
#endif

    /// <param name="vex">The running Vex, for the "Right now" buttons. Null on first run, before he exists, which hides them.</param>
    public SetupWindow(VexWindow? vex = null)
    {
        InitializeComponent();
        var c = Store.Config;

        NameBox.Text = c.UserName.Length > 0 ? c.UserName : GuessName();
        WorkBox.Text = c.Workplace;

#if LITE
        LiteLayout();
#else
        UseNotebook.IsEnabled = notebookPath != null;
        NotebookNote.Text = notebookPath != null ? $"Found: {notebookPath}" : "Not installed on this PC.";
        bool useNotebook = notebookPath != null && c.NotesMode != "local";
        UseNotebook.IsChecked = useNotebook;
        UseLocal.IsChecked = !useNotebook;
        FolderBox.Text = c.NotesFolder ?? Services.Hub.DefaultNotesFolder;
        Browse.Click += (_, _) =>
        {
            using var dlg = new System.Windows.Forms.FolderBrowserDialog { SelectedPath = FolderBox.Text, Description = "Folder for Vex's notes" };
            if (dlg.ShowDialog() == System.Windows.Forms.DialogResult.OK) { FolderBox.Text = dlg.SelectedPath; UseLocal.IsChecked = true; }
        };
#endif

        Startup.IsChecked = App.StartsWithWindows() || !c.SetupDone;
        Knock.IsChecked = c.KnockSound;
#if !LITE
        bool claude = ClaudeCodeBrain.FindClaude() != null;
        ClaudeHooks.IsEnabled = claude;
        ClaudeHooks.IsChecked = claude && (Services.ClaudeWatch.Installed() || !c.SetupDone);
#endif
        ChatClickAway.IsChecked = c.ChatClosesOnClickAway;
        CodingAtLaptop.IsChecked = c.CodingAtLaptop;
        CodingForClaude.IsChecked = c.CodingForClaude;
        LunchRoutine.IsChecked = c.LunchRoutine;
        LunchMinutes.Text = c.LunchMinutes.ToString(CultureInfo.CurrentCulture);
        foreach (var a in IdleActs.All)
            IdleActsList.Children.Add(new System.Windows.Controls.CheckBox
            {
                Content = a.Name, Tag = a.Id, IsChecked = IdleActs.Enabled(a.Act), Width = 200,
            });
        ActivityLog.IsChecked = c.ActivityLog;
        Curious.IsChecked = c.Curious;
        WatchApps.Text = string.Join(", ", c.WatchContentApps);
        LeaveAt.Text = c.LeaveAt;
        WorkAddress.Text = c.WorkAddress;
        HomeAddress.Text = c.HomeAddress;
        TomTomKey.Text = c.TomTomKey;
        (c.CommuteBy switch { "transit" => ByTransit, "cycle" => ByCycle, "walk" => ByWalk, _ => ByDrive }).IsChecked = true;

#if !LITE
        PrivacyText.Text =
            "Everything I keep stays on this PC (in %LOCALAPPDATA%\\Vex and your notes). I never record password managers, " +
            "private browsing or windows with words like \"password\" or \"bank\" in the title. When you chat with me, your " +
            "message goes to Claude with a short summary of your day: time at the desk, recent apps (and window titles if the " +
            "log is on), meetings, to-dos and my notes, plus the text of the window in front only for apps you list above. " +
            "Please check that using Claude this way is OK where you work.";
        ClaudeNote.Text = claude
            // The tray used to show "connected ✓"; this checkbox is where connecting lives now, so say it here.
            ? "Claude Code found: chat uses your own Claude login." + (Services.ClaudeWatch.Installed() ? " Alerts connected ✓" : "")
            : "Claude Code isn't installed, so I can't chat yet. Everything else works. Install Claude Code and sign in, then I'll pick it up.";

        OpenMemory.Click += (_, _) => App.Open(Services.Memory.Folder);
        OpenChats.Click += (_, _) => App.Open(ChatLog.Folder);
#endif
        OpenConfig.Click += (_, _) => App.Open(Path.Combine(Store.Dir, "config.json"));
        OpenData.Click += (_, _) => App.Open(Store.Dir);

        if (vex != null) WireRightNow(vex);
        else RightNow.Visibility = Visibility.Collapsed;

        // First run walks in with "Off we go"; afterwards it's just a settings window.
        if (c.SetupDone) { Done.Content = "Save"; Title = "Vex settings"; }
        Done.Click += (_, _) => Save();
    }

    /// <summary>
    /// Behaviour → Right now: the actions that used to be on the right-click and tray menus, doing exactly what
    /// they did there. Labels flip (Lunch break / End lunch, nap / wake, hide / show) after each click.
    /// </summary>
    void WireRightNow(VexWindow vex)
    {
        void Refresh()
        {
            // As on the old menu: End lunch while at lunch, Lunch break only with the routine on (as saved), no nap at lunch.
            LunchNow.Content = vex.OnLunch ? "End lunch" : "Lunch break";
            LunchNow.Visibility = vex.OnLunch || Store.Config.LunchRoutine ? Visibility.Visible : Visibility.Collapsed;
            NapNow.Content = vex.Asleep ? "Wake up" : "Have a nap";
            NapNow.Visibility = vex.OnLunch ? Visibility.Collapsed : Visibility.Visible;
            HideNow.Content = vex.IsHidden ? "Show Vex" : "Hide Vex";
        }
        void On(System.Windows.Controls.Button b, Action a) => b.Click += (_, _) => { a(); Refresh(); };

        On(LunchNow, () => { if (vex.OnLunch) vex.LunchBack(); else vex.StartLunch(); });
        On(NapNow, vex.ToggleNap);
        On(CoffeeNow, vex.RefillCoffee);
        On(OtherScreen, vex.GoToOtherScreen);
        On(HideNow, vex.ToggleHidden);
#if !LITE
        On(ForgetNow, vex.ForgetChat);
#endif
        // The chat can't be used while this modal window is up, so close first and open it once we're gone.
        ChatNow.Click += (_, _) => { Closed += (_, _) => vex.OpenChat(); Close(); };
        Refresh();
    }

#if LITE
    /// <summary>
    /// The lite build shares this window's layout with the full Vex: what needs Claude (chat, notes, watching, the
    /// coding laptop, Claude Code hooks, memory) is hidden, the TomTom key too (nothing leaves the PC), and the words
    /// that mention chatting say what the panel does instead.
    /// </summary>
    void LiteLayout()
    {
        foreach (var e in new UIElement[] { WorkPart, NotesTab, WatchTab, ClaudeHooks, ClaudeNote, CodingAtLaptop, CodingForClaude, CodingNote,
                                            TomTomLabel, TomTomKey, ForgetNow, OpenMemory, OpenChats })
            e.Visibility = Visibility.Collapsed;
        IntroNote.Text = "I live on your taskbar, keep out of the way and nudge you about breaks, water, lunch and meetings. " +
                         "Click me to set reminders.";
        ChatClickAway.Content = "Close my reminders panel when you click outside it";
        ChatClickAwayNote.Text = "Anything half-typed is kept for next time.";
        ChatNow.Content = "Reminders";
        LunchNote.Text = "Starts when you press Going on the lunch nudge, or Going to lunch in my panel. Come back early and I'll wake up.";
        RightNowNote.Text = "These happen straight away. Reminders closes this window first (without saving).";
        HomeNote.Text = "15 minutes before you leave I'll tell you to pack up, with a Google Maps button for the way home " +
                        "(by train or bus it lists the next departures). The addresses only go into that map link. Postcodes are fine.";
        PrivacyText.Text =
            "Everything I keep stays on this PC, in %LOCALAPPDATA%\\Vex Lite: settings, reminders and today's counts. Nothing is " +
            "sent anywhere. I notice which app is in front (in memory only, never saved) for the long-stretch check, and " +
            "whether the microphone is in use so I keep quiet in calls. Meetings come from classic Outlook, if it's running.";
        OpenConfig.Click += (_, _) => App.Open(Path.Combine(Store.Dir, "config.json"));
        OpenData.Click += (_, _) => App.Open(Store.Dir);
    }
#endif

    [System.Runtime.InteropServices.DllImport("secur32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    static extern bool GetUserNameEx(int format, System.Text.StringBuilder name, ref uint size);

    /// <summary>First name from the Windows display name ("Ben Summers" → "Ben"), else from the login ("ben.summers" → "Ben").</summary>
    static string GuessName()
    {
        string first = "";
        try
        {
            uint size = 256; var sb = new System.Text.StringBuilder((int)size);
            if (GetUserNameEx(3 /* NameDisplay */, sb, ref size)) first = sb.ToString().Split(' ', ',')[0];
        }
        catch { }
        if (first.Length == 0) first = Environment.UserName.Split('.', '_', ' ')[0];
        return first.Length == 0 ? "" : CultureInfo.CurrentCulture.TextInfo.ToTitleCase(first.ToLowerInvariant());
    }

    void Save()
    {
        var c = Store.Config;
        c.UserName = NameBox.Text.Trim();
#if !LITE
        c.Workplace = WorkBox.Text.Trim();
        c.NotesMode = UseNotebook.IsChecked == true ? "notebook" : "local";
        c.NotesFolder = UseLocal.IsChecked == true ? FolderBox.Text.Trim() : c.NotesFolder;
#endif
        c.KnockSound = Knock.IsChecked == true;
        c.ChatClosesOnClickAway = ChatClickAway.IsChecked == true;
#if !LITE
        c.CodingAtLaptop = CodingAtLaptop.IsChecked == true;
        c.CodingForClaude = CodingForClaude.IsChecked == true;
#endif
        c.LunchRoutine = LunchRoutine.IsChecked == true;
        // Anything unreadable keeps the old length; a lunch shorter than a couple of minutes isn't one.
        if (double.TryParse(LunchMinutes.Text.Trim(), NumberStyles.Float, CultureInfo.CurrentCulture, out var lunch) && lunch > 0)
            c.LunchMinutes = Math.Clamp(lunch, 2, 240);
        // Every activity written out, on or off, so config.json shows the full list to edit.
        foreach (var box in IdleActsList.Children.OfType<System.Windows.Controls.CheckBox>())
            c.IdleActivities[(string)box.Tag] = box.IsChecked == true;
#if !LITE
        c.ActivityLog = ActivityLog.IsChecked == true;
        c.Curious = Curious.IsChecked == true;
        c.WatchContentApps = WatchApps.Text.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Select(a => a.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? a[..^4] : a).ToList();
#endif
        c.LeaveAt = Services.Commute.ParseTime(LeaveAt.Text) is { } leave ? $"{leave:hh\\:mm}" : "";
        c.WorkAddress = WorkAddress.Text.Trim();
        c.HomeAddress = HomeAddress.Text.Trim();
#if !LITE
        c.TomTomKey = TomTomKey.Text.Trim();
#endif
        c.CommuteBy = ByTransit.IsChecked == true ? "transit" : ByCycle.IsChecked == true ? "cycle" : ByWalk.IsChecked == true ? "walk" : "drive";
        c.SetupDone = true;
        Store.SaveConfig();

#if !LITE
        if (c.NotesMode == "local") { try { Directory.CreateDirectory(c.NotesFolder ?? Services.Hub.DefaultNotesFolder); } catch { } }
        App.SetStartup(Startup.IsChecked == true);
        try
        {
            if (ClaudeHooks.IsChecked == true) Services.ClaudeWatch.Install();
            else if (Services.ClaudeWatch.Installed()) Services.ClaudeWatch.Uninstall();
        }
        catch (Exception ex) { Store.Log($"Claude hooks: {ex.Message}"); }
#else
        App.SetStartup(Startup.IsChecked == true);
#endif
        DialogResult = true;
    }
}
