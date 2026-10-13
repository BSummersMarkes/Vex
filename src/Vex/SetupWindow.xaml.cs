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
    readonly string? notebookPath = Services.Notebook.Detect();

    public SetupWindow()
    {
        InitializeComponent();
        var c = Store.Config;

        NameBox.Text = c.UserName.Length > 0 ? c.UserName : GuessName();
        WorkBox.Text = c.Workplace;

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

        Startup.IsChecked = App.StartsWithWindows() || !c.SetupDone;
        Knock.IsChecked = c.KnockSound;
        bool claude = ClaudeCodeBrain.FindClaude() != null;
        ClaudeHooks.IsEnabled = claude;
        ClaudeHooks.IsChecked = claude && (Services.ClaudeWatch.Installed() || !c.SetupDone);
        ActivityLog.IsChecked = c.ActivityLog;
        Curious.IsChecked = c.Curious;
        WatchApps.Text = string.Join(", ", c.WatchContentApps);
        LeaveAt.Text = c.LeaveAt;
        WorkAddress.Text = c.WorkAddress;
        HomeAddress.Text = c.HomeAddress;
        TomTomKey.Text = c.TomTomKey;

        PrivacyText.Text =
            "Everything I keep stays on this PC (in %LOCALAPPDATA%\\Vex and your notes). I never record password managers, " +
            "private browsing or windows with words like \"password\" or \"bank\" in the title. When you chat with me, your " +
            "message goes to Claude with a short summary of your day: time at the desk, recent apps (and window titles if the " +
            "log is on), meetings, to-dos and my notes, plus the text of the window in front only for apps you list above. " +
            "Please check that using Claude this way is OK where you work.";
        ClaudeNote.Text = claude
            ? "Claude Code found: chat uses your own Claude login."
            : "Claude Code isn't installed, so I can't chat yet. Everything else works. Install Claude Code and sign in, then I'll pick it up.";

        // First run walks in with "Off we go"; afterwards it's just a settings window.
        if (c.SetupDone) { Done.Content = "Save"; Title = "Vex settings"; }
        Done.Click += (_, _) => Save();
    }

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
        c.Workplace = WorkBox.Text.Trim();
        c.NotesMode = UseNotebook.IsChecked == true ? "notebook" : "local";
        c.NotesFolder = UseLocal.IsChecked == true ? FolderBox.Text.Trim() : c.NotesFolder;
        c.KnockSound = Knock.IsChecked == true;
        c.ActivityLog = ActivityLog.IsChecked == true;
        c.Curious = Curious.IsChecked == true;
        c.WatchContentApps = WatchApps.Text.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Select(a => a.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? a[..^4] : a).ToList();
        c.LeaveAt = Services.Commute.ParseTime(LeaveAt.Text) is { } leave ? $"{leave:hh\\:mm}" : "";
        c.WorkAddress = WorkAddress.Text.Trim();
        c.HomeAddress = HomeAddress.Text.Trim();
        c.TomTomKey = TomTomKey.Text.Trim();
        c.SetupDone = true;
        Store.SaveConfig();

        if (c.NotesMode == "local") { try { Directory.CreateDirectory(c.NotesFolder ?? Services.Hub.DefaultNotesFolder); } catch { } }
        App.SetStartup(Startup.IsChecked == true);
        try
        {
            if (ClaudeHooks.IsChecked == true) Services.ClaudeWatch.Install();
            else if (Services.ClaudeWatch.Installed()) Services.ClaudeWatch.Uninstall();
        }
        catch (Exception ex) { Store.Log($"Claude hooks: {ex.Message}"); }
        DialogResult = true;
    }
}
