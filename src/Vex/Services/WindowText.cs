using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Automation;

namespace Vex.Services;

/// <summary>
/// Reads the text of the window in front, but only for apps on WatchContentApps in config.json (empty by default,
/// so it never happens unless you list an app). Uses UI Automation, the accessibility interface, so nothing is
/// screenshotted. Private windows (exclusion list) are never read, and password fields are skipped.
/// </summary>
public static class WindowText
{
    /// <summary>Whether this app's window text may be read.</summary>
    public static bool Allowed(string app) =>
        app.Length > 0 && app is not (Activity.Away or Activity.Private)
        && Store.Config.WatchContentApps.Any(a => a.Equals(app, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Text of the last window you were working in (not Vex's own), if its app is allowlisted; else null.
    /// Runs off the UI thread and gives up after a few seconds (big documents and browsers can be slow).
    /// </summary>
    public static async Task<string?> ReadInFrontAsync()
    {
        var a = Hub.Activity;
        if (!Allowed(a.CurrentApp) || a.CurrentHwnd == IntPtr.Zero || !Native.IsWindow(a.CurrentHwnd)) return null;
        var hwnd = a.CurrentHwnd;
        // The activity tick is up to 5 s old: re-check this exact window's app and title now, so a tab that just
        // switched to banking or private browsing isn't read.
        Native.GetWindowThreadProcessId(hwnd, out uint pid);
        string app;
        try { app = System.Diagnostics.Process.GetProcessById((int)pid).ProcessName; } catch { return null; }
        if (!Allowed(app) || Activity.IsPrivate(app, Native.WindowText(hwnd))) return null;
        int max = Math.Max(200, Store.Config.WatchContentChars);
        var read = Task.Run(() => Read(hwnd, max));
        if (await Task.WhenAny(read, Task.Delay(TimeSpan.FromSeconds(4))) != read) { Store.Log("Window text: timed out"); return null; }
        return read.Result is { Length: > 0 } t ? t : null;
    }

    static string? Read(IntPtr hwnd, int max)
    {
        try
        {
            var root = AutomationElement.FromHandle(hwnd);
            var notPassword = new PropertyCondition(AutomationElement.IsPasswordProperty, false);

            // Documents and editors (Word, Notepad, browsers' page content) expose the whole text at once.
            var doc = root.FindFirst(TreeScope.Descendants, new AndCondition(
                new PropertyCondition(AutomationElement.IsTextPatternAvailableProperty, true), notPassword));
            if (doc?.GetCurrentPattern(TextPattern.Pattern) is TextPattern tp)
            {
                var text = Tidy(tp.DocumentRange.GetText(max));
                if (text.Length > 40) return text;
            }

            // Otherwise collect the visible labels and values: forms, lists, grids.
            var sb = new StringBuilder();
            var seen = new HashSet<string>();
            var parts = root.FindAll(TreeScope.Descendants, new AndCondition(notPassword, new OrCondition(
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Text),
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Edit),
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.DataItem),
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ListItem))));
            int n = 0;
            foreach (AutomationElement e in parts)
            {
                if (++n > 600 || sb.Length >= max) break;
                var s = e.Current.Name;
                if (e.TryGetCurrentPattern(ValuePattern.Pattern, out var vp) && vp is ValuePattern v && v.Current.Value is { Length: > 0 } val) s = $"{s}: {val}";
                s = s.Trim();
                if (s.Length > 0 && seen.Add(s)) sb.AppendLine(s);
            }
            var all = Tidy(sb.ToString());
            return all.Length > max ? all[..max] + "…" : all;
        }
        catch (Exception ex) { Store.Log($"Window text: {ex.Message}"); return null; }
    }

    /// <summary>Squashes runs of blank lines and spaces so the budget goes on words.</summary>
    static string Tidy(string s) =>
        System.Text.RegularExpressions.Regex.Replace(System.Text.RegularExpressions.Regex.Replace(s, @"[ \t]+", " "), @"(\s*\n){3,}", "\n\n").Trim();
}
