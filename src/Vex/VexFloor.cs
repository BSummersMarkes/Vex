using System;
using System.Linq;
using System.Windows.Interop;
using System.Windows.Media;
using Microsoft.Win32;
using Forms = System.Windows.Forms;

namespace Vex;

/// <summary>
/// Keeping his feet on the taskbar. The floor is the bottom of the working area (the screen minus the taskbar) of the
/// monitor he's on, whatever side the taskbar is on, so his lowest point is the top of a bottom taskbar, and the
/// screen's bottom edge when it's on the side, at the top, or auto-hidden.
///
/// He used to end up with only his top half showing, three ways: ducking out of the way ("peeking") deliberately
/// sank him 86 DIP below the floor, past the bottom of the screen; his window was placed assuming it was drawn at
/// the monitor's DPI, so after a DPI change (another monitor, a Remote Desktop session) it could be drawn bigger than
/// that and his feet hung below the floor; and the monitor list was only re-read on display-settings changes, from
/// WinForms' cached copy, so a taskbar that moved, grew or stopped auto-hiding could leave him standing on a floor
/// that wasn't there any more. Now: peeking stands on the floor, the window is placed from where his boots are
/// actually drawn, the monitors are read fresh on every change Windows reports and every ScreenCheckSeconds besides,
/// and every frame nothing but your hand can hold him below the floor.
/// </summary>
public partial class VexWindow
{
    /// <summary>Bottom of his boot soles in the window, in DIPs (the Rig's top, 110, plus the soles at 148.5).</summary>
    const double SoleY = 258.5;

    double nextScreenCheck, screenBurstUntil;
    string screenKey = "";
    uint taskbarCreated;

    /// <summary>
    /// Re-reads the monitors from Windows when anything about them may have changed: taskbar moved, resized or
    /// auto-hide toggled, resolution or scaling, a monitor plugged in or out, docking, Remote Desktop connecting or
    /// disconnecting, Explorer restarting. Called once the window exists.
    /// </summary>
    void WatchScreens()
    {
        taskbarCreated = Native.RegisterWindowMessage("TaskbarCreated");
        HwndSource.FromHwnd(hwnd)?.AddHook((IntPtr h, int msg, IntPtr w, IntPtr l, ref bool handled) =>
        {
            // Queued so WPF has dealt with the message first (a DPI change resizes the window).
            if (msg is Native.WM_SETTINGCHANGE or Native.WM_DISPLAYCHANGE or Native.WM_DPICHANGED || (msg == taskbarCreated && msg != 0))
                Dispatcher.BeginInvoke(() => ScreensChanged("window message"));
            return IntPtr.Zero;
        });
        // These can arrive on Windows' own system-events thread, so they're queued too (see the constructor).
        SystemEvents.DisplaySettingsChanged += (_, _) => Dispatcher.BeginInvoke(() => ScreensChanged("display settings"));
        SystemEvents.SessionSwitch += (_, e) => Dispatcher.BeginInvoke(() => ScreensChanged($"session: {e.Reason}"));
        SystemEvents.PowerModeChanged += (_, e) => { if (e.Mode == PowerModes.Resume) Dispatcher.BeginInvoke(() => ScreensChanged("resume")); };
        RefreshScreens("start");
    }

    /// <summary>
    /// Something changed. Windows (and Remote Desktop especially) often moves the taskbar about for a few seconds
    /// after telling us, so keep looking closely for a while, not just once.
    /// </summary>
    void ScreensChanged(string why)
    {
        screenBurstUntil = now + 6;
        RefreshScreens(why);
    }

    /// <summary>Called every frame: re-reads the monitors on the safety-net timer, more often just after a change.</summary>
    void CheckScreens()
    {
        if (now < nextScreenCheck) return;
        RefreshScreens(null);
    }

    /// <summary>
    /// Fresh monitors, straight from Windows. WinForms' Screen.AllScreens is a cached list whose working areas are
    /// only updated when WinForms hears of a change itself, so each one is made anew (Screen.FromPoint builds a new
    /// Screen that reads its working area on first use). If anything moved, he's put back on the floor at once.
    /// </summary>
    void RefreshScreens(string? why)
    {
        nextScreenCheck = now + (now < screenBurstUntil ? 0.25 : Math.Max(0.25, Store.Config.ScreenCheckSeconds));
        try
        {
            var fresh = Native.MonitorRects()
                .Select(r => Forms.Screen.FromPoint(new System.Drawing.Point(r.Left + (r.Right - r.Left) / 2, r.Top + (r.Bottom - r.Top) / 2)))
                .ToArray();
            if (fresh.Length == 0) fresh = Forms.Screen.AllScreens;
            string key = string.Join(" | ", fresh.Select(s => $"{s.Bounds} work {s.WorkingArea}")) + $" dpi {S * 96:0}";
            screens = fresh;
            if (key == screenKey) return;
            if (why != null || screenKey != "") Store.Log($"Screens ({why ?? "check"}): {key}");
            screenKey = key;
        }
        catch (Exception e) { Store.Log($"Screens: couldn't re-read the monitors: {e.Message}"); return; }
        KeepAboveFloor();
        Place(force: true);
    }

    /// <summary>
    /// The one rule every state goes through, once a frame and after any monitor change: his feet are never below
    /// the floor of the monitor he's on. Covers startup, walking, sitting, lunch and its nap, climbing, falls and
    /// drops, perching and hops alike. Only while you're carrying him can he go lower; let go, and he's back up.
    /// </summary>
    void KeepAboveFloor()
    {
        if (mode == Mode.Dragging) return;
        double floor = ScreenAt(x).WorkingArea.Bottom;
        if (mode == Mode.Climbing)
        {
            // The taskbar grew mid-climb: the way down now ends on the new floor, so he still finishes there.
            climbFromY = Math.Min(climbFromY, floor);
            climbToY = Math.Min(climbToY, floor);
        }
        if (y > floor) y = floor;
    }

    /// <summary>
    /// The scale his figure is actually drawn at in his window, which is what decides where his boots end up on the
    /// screen. Normally the monitor's DPI scale (S), but straight after a DPI change it can differ for a while.
    /// </summary>
    double DrawnScale()
    {
        try { double k = VisualTreeHelper.GetDpi(this).DpiScaleY; return k > 0 ? k : S; }
        catch { return S; }
    }

    /// <summary>
    /// If the window's real size doesn't match the size his figure is drawn at (seen after Remote Desktop and DPI
    /// changes), the bottom of the drawing, his boots, gets cut off. Put it back to the right size.
    /// </summary>
    void FixWindowSize(double k)
    {
        if (!Native.GetWindowRect(hwnd, out var r)) return;
        int w = (int)Math.Round(WinW * k), h = (int)Math.Round(WinH * k);
        if (Math.Abs(r.Right - r.Left - w) <= 1 && Math.Abs(r.Bottom - r.Top - h) <= 1) return;
        Store.Log($"Window was {r.Right - r.Left}x{r.Bottom - r.Top}, drawn for {w}x{h}: resized.");
        Native.SetWindowPos(hwnd, Native.HWND_TOPMOST, r.Left, r.Top, w, h, Native.SWP_NOACTIVATE);
    }
}
