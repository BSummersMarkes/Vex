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
        WireBirthday(vex);

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
        WireLightness();
        WireOutfits(vex);
        ActivityLog.IsChecked = c.ActivityLog;
        Curious.IsChecked = c.Curious;
        WatchApps.Text = string.Join(", ", c.WatchContentApps);
        LeaveAt.Text = c.LeaveAt;
        // Working days only; a weekend entry can still go in config.json, and Save leaves it alone.
        foreach (var day in new[] { "Mon", "Tue", "Wed", "Thu", "Fri" })
        {
            LeaveDays.Children.Add(new System.Windows.Controls.TextBlock { Text = day, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 4, 0) });
            LeaveDays.Children.Add(new System.Windows.Controls.TextBox
            {
                Tag = day, Width = 46, Margin = new Thickness(0, 0, 10, 0),
                Text = c.LeaveAtByDay.TryGetValue(day, out var dayLeave) ? dayLeave : "",
            });
        }
        WorkAddress.Text = c.WorkAddress;
        HomeAddress.Text = c.HomeAddress;
        TomTomKey.Text = c.TomTomKey;
        (c.CommuteBy switch { "transit" => ByTransit, "cycle" => ByCycle, "walk" => ByWalk, _ => ByDrive }).IsChecked = true;

#if !LITE
        ClockNudges.IsChecked = c.ClockNudges;
        ClockOutAt.Text = c.ClockOutAt;
        WireNetworks();
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

    /// <summary>
    /// You → birthday: day and month drop-downs (the day list follows the month, so there's no 31 February) and an
    /// optional year. Clear empties them; Save keeps whatever's there. Try it now (only once Vex is running) shows the
    /// greeting, confetti and party hat straight away without using up the year's greeting.
    /// </summary>
    void WireBirthday(VexWindow? vex)
    {
        var c = Store.Config;
        BirthMonth.Items.Add("–");
        foreach (var m in CultureInfo.CurrentCulture.DateTimeFormat.MonthNames.Take(12)) BirthMonth.Items.Add(m);
        // Item index = day / month number; 0 is the "–" for not set.
        void FillDays()
        {
            int keep = BirthDay.SelectedIndex, max = BirthMonth.SelectedIndex > 0 ? DateTime.DaysInMonth(2024, BirthMonth.SelectedIndex) : 31;
            BirthDay.Items.Clear();
            BirthDay.Items.Add("–");
            for (int d = 1; d <= max; d++) BirthDay.Items.Add(d.ToString(CultureInfo.CurrentCulture));
            BirthDay.SelectedIndex = Math.Clamp(keep, 0, max);
        }
        bool set = Birthdays.Set;
        BirthMonth.SelectedIndex = set ? c.BirthdayMonth : 0;
        FillDays();
        BirthDay.SelectedIndex = set ? c.BirthdayDay : 0;
        BirthYear.Text = set && c.BirthdayYear > 0 ? c.BirthdayYear.ToString(CultureInfo.InvariantCulture) : "";
        BirthMonth.SelectionChanged += (_, _) => FillDays();
        ClearBirthday.Click += (_, _) => { BirthMonth.SelectedIndex = 0; BirthDay.SelectedIndex = 0; BirthYear.Text = ""; };
        if (vex != null) TryBirthday.Click += (_, _) => vex.BirthdayPreview();
        else TryBirthday.Visibility = Visibility.Collapsed;
        // Saved with no birthday: a hat still on from Try it now comes off as well.
        Closed += (_, _) => { if (DialogResult == true && !Birthdays.Set) vex?.EndBirthdayPreview(); };
    }

    /// <summary>
    /// Theme → Hoodie lightness: Vex (and the swatch) repaint as the slider moves, as a live preview. Only Save keeps
    /// it; closing any other way puts back the lightness he had when the window opened.
    /// </summary>
    void WireLightness()
    {
        double before = Store.Config.HoodieLightness;
        void Show()
        {
            HoodieSwatch.Background = Theme.Brush(Theme.Hoodie);
            LightnessValue.Text = Lightness.Value == 0 ? "0" : Lightness.Value.ToString("+0;−0", CultureInfo.CurrentCulture);
        }
        Lightness.Minimum = Theme.MinLightness;
        Lightness.Maximum = Theme.MaxLightness;
        Lightness.Value = Math.Clamp(Math.Round(before), Theme.MinLightness, Theme.MaxLightness);
        Show();
        Lightness.ValueChanged += (_, e) => { Theme.PreviewLightness(e.NewValue); Show(); };
        Closed += (_, _) => { if (DialogResult != true && Store.Config.HoodieLightness != before) Theme.PreviewLightness(before); };
    }

    /// <summary>
    /// Theme → Seasonal outfits: the on/off switch (kept on Save), and "Try one on", which puts the outfit on him at once
    /// as a preview, like the lightness slider. Only Save keeps the choice; closing any other way puts back what was set.
    /// </summary>
    void WireOutfits(VexWindow? vex)
    {
        var c = Store.Config;
        string before = c.DressUpPreview;
        DressUp.IsChecked = c.DressUp;
        var names = Occasions.All.Select(o => o.Name).ToList();
        string list = names.Count > 1 ? string.Join(", ", names.Take(names.Count - 1)) + " and " + names[^1] : string.Join("", names);
        DressUpNote.Text = $"{list}: from {c.DressUpLeadDays} days before until the end of the day itself. " +
                           "The first time each year I'll say something about it. Once.";
        // The birthday party hat is in the list too (Wearable), so it can be tried on without waiting for the day.
        foreach (var (id, name) in new[] { ("", "Go by the date") }.Concat(Occasions.Wearable.Select(o => (o.Id, o.Name))))
        {
            var choice = new System.Windows.Controls.RadioButton
            {
                Content = name, GroupName = "OutfitPreview", Margin = new Thickness(0, 5, 14, 0),
                IsChecked = string.Equals(id, before, StringComparison.OrdinalIgnoreCase) || (id == "" && Occasions.Find(before) == null),
            };
            choice.Checked += (_, _) => { c.DressUpPreview = id; vex?.RefreshOutfit(); };
            OutfitPreview.Children.Add(choice);
        }
        Closed += (_, _) =>
        {
            if (DialogResult != true) c.DressUpPreview = before;
            vex?.RefreshOutfit();   // saved or not, he wears whatever the settings now say
        };
    }

#if !LITE
    /// <summary>Working copies of the saved networks; written to config.json only on Save, like everything else here.</summary>
    readonly System.Collections.Generic.List<NetPrint> homeNets = Store.Config.HomeNetworks.ToList(),
                                                       officeNets = Store.Config.OfficeNetworks.ToList();

    /// <summary>
    /// Home days → "This network is home" / "…is the office": saves what this PC sees right now under that place
    /// (moving it if it was saved under the other one), and lists what's saved with a ✕ each.
    /// </summary>
    void WireNetworks()
    {
        // Today's verdict only once Vex is running (not on first run, before the Hub has looked).
        var here = Services.Whereabouts.Observe();
        NetNow.Text = $"This PC is on: {Services.Whereabouts.Describe(here)}." +
                      (Services.Hub.Vex != null ? " " + Services.Hub.Where.Summary(details: true) : "");
        void ShowList()
        {
            NetList.Children.Clear();
            foreach (var (place, nets) in new[] { ("Home", homeNets), ("Office", officeNets) })
                foreach (var net in nets.ToList())
                {
                    var row = new System.Windows.Controls.DockPanel { Margin = new Thickness(0, 2, 0, 0) };
                    var remove = new System.Windows.Controls.Button { Content = "✕", Padding = new Thickness(6, 0, 6, 0), Margin = new Thickness(8, 0, 0, 0), ToolTip = "Forget this network" };
                    remove.Click += (_, _) => { nets.Remove(net); ShowList(); };
                    System.Windows.Controls.DockPanel.SetDock(remove, System.Windows.Controls.Dock.Right);
                    row.Children.Add(remove);
                    row.Children.Add(new System.Windows.Controls.TextBlock
                    {
                        Text = $"{place}: {Services.Whereabouts.Describe(net)}", VerticalAlignment = VerticalAlignment.Center,
                    });
                    NetList.Children.Add(row);
                }
            if (NetList.Children.Count == 0)
                NetList.Children.Add(new System.Windows.Controls.TextBlock { Text = "No networks saved yet, so I can't tell home from the office.", Style = (Style)FindResource("Note") });
        }
        void SaveHere(System.Collections.Generic.List<NetPrint> into, System.Collections.Generic.List<NetPrint> other)
        {
            here = Services.Whereabouts.Observe();   // fresh: the network may have changed since the window opened
            if (here.WiFi.Count == 0 && here.Gateways.Count == 0 && here.DnsSuffixes.Count == 0)
            {
                NetNow.Text = "I can't see a network to save right now (no Wi-Fi name, router or DNS suffix).";
                return;
            }
            // The same network saved twice (or under both places) would only muddle the matching.
            static string Key(NetPrint p) => string.Join("|", p.WiFi.Concat(p.Gateways).Concat(p.DnsSuffixes).Select(s => s.ToLowerInvariant()).OrderBy(s => s)) + p.Vpn;
            into.RemoveAll(p => Key(p) == Key(here));
            other.RemoveAll(p => Key(p) == Key(here));
            into.Add(here);
            NetNow.Text = $"Saved as {(into == homeNets ? "home" : "the office")}: {Services.Whereabouts.Describe(here)}. Save keeps it.";
            ShowList();
        }
        SaveHomeNet.Click += (_, _) => SaveHere(homeNets, officeNets);
        SaveOfficeNet.Click += (_, _) => SaveHere(officeNets, homeNets);
        ShowList();
    }
#endif

#if LITE
    /// <summary>
    /// The lite build shares this window's layout with the full Vex: what needs Claude (chat, notes, watching, the
    /// coding laptop, Claude Code hooks, memory) is hidden, the TomTom key too (nothing leaves the PC), and the words
    /// that mention chatting say what the panel does instead.
    /// </summary>
    void LiteLayout()
    {
        foreach (var e in new UIElement[] { WorkPart, NotesTab, WatchTab, ClaudeHooks, ClaudeNote, CodingAtLaptop, CodingForClaude, CodingNote,
                                            TomTomLabel, TomTomKey, ForgetNow, OpenMemory, OpenChats, HomeDaysTab })
            e.Visibility = Visibility.Collapsed;
        IntroNote.Text = "I live on your taskbar, keep out of the way and nudge you about breaks, water, lunch and meetings. " +
                         "Click me to set reminders.";
        ChatClickAway.Content = "Close my reminders panel when you click outside it";
        ChatClickAwayNote.Text = "Anything half-typed is kept for next time.";
        ChatNow.Content = "Reminders";
        LunchNote.Text = "Starts when you press Going on the lunch nudge, or Going to lunch in my panel. Come back early and I'll wake up.";
        RightNowNote.Text = "These happen straight away. Reminders closes this window first (without saving).";
        HomeNote.Text = "15 minutes before you leave that day I'll tell you to pack up, with a Google Maps button for the way home " +
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
        // Half a date isn't a birthday: without both day and month it's cleared. A year that can't be (the future, or
        // 29 Feb in a year without one) is just dropped; it's only ever used for his age.
        int bMonth = BirthMonth.SelectedIndex, bDay = BirthDay.SelectedIndex;
        if (bMonth <= 0 || bDay <= 0) bMonth = bDay = 0;
        int bYear = bMonth > 0 && int.TryParse(BirthYear.Text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var y)
                    && y >= 1900 && y <= DateTime.Today.Year && bDay <= DateTime.DaysInMonth(y, bMonth) ? y : 0;
        bool birthdayMoved = bMonth != c.BirthdayMonth || bDay != c.BirthdayDay;
        c.BirthdayDay = bDay; c.BirthdayMonth = bMonth; c.BirthdayYear = bYear;
        if (birthdayMoved) Birthdays.Changed();
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
        c.HoodieLightness = Lightness.Value;   // already previewing; this makes it stick
        c.DressUp = DressUp.IsChecked == true;  // the try-on choice is already in DressUpPreview
#if !LITE
        c.ActivityLog = ActivityLog.IsChecked == true;
        c.Curious = Curious.IsChecked == true;
        c.WatchContentApps = WatchApps.Text.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Select(a => a.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? a[..^4] : a).ToList();
#endif
        c.LeaveAt = Services.Commute.ParseTime(LeaveAt.Text) is { } leave ? $"{leave:hh\\:mm}" : "";
        // Cleared days are written as "" rather than removed, so the Friday default can't come back on the next load.
        foreach (var box in LeaveDays.Children.OfType<System.Windows.Controls.TextBox>())
            c.LeaveAtByDay[(string)box.Tag] = Services.Commute.ParseTime(box.Text) is { } dayLeave ? $"{dayLeave:hh\\:mm}" : "";
        c.WorkAddress = WorkAddress.Text.Trim();
        c.HomeAddress = HomeAddress.Text.Trim();
#if !LITE
        c.TomTomKey = TomTomKey.Text.Trim();
        c.ClockNudges = ClockNudges.IsChecked == true;
        c.ClockOutAt = Services.Commute.ParseTime(ClockOutAt.Text) is { } clockOut ? $"{clockOut:hh\\:mm}" : "";
        c.HomeNetworks = homeNets;
        c.OfficeNetworks = officeNets;
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
