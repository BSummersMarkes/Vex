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
///
/// Later (2026-10-23) that rule was widened to the whole work area. He's now kept on it sideways as well as up and
/// down (a lower resolution or a Remote Desktop session could leave him off the edge of a smaller screen). Even while
/// you carry him he can't be pushed through the taskbar. If Windows moves his window by itself, it's put back.
/// Monitors sleeping or waking count as a change. Every correction goes in vex.log with its reason and both positions.
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
    /// disconnecting, locking and unlocking, monitors sleeping or waking, the PC resuming, Explorer restarting.
    /// Called once the window exists.
    /// </summary>
    void WatchScreens()
    {
        taskbarCreated = Native.RegisterWindowMessage("TaskbarCreated");
        HwndSource.FromHwnd(hwnd)?.AddHook((IntPtr h, int msg, IntPtr w, IntPtr l, ref bool handled) =>
        {
            // Queued so WPF has dealt with the message first (a DPI change resizes the window).
            if (msg is Native.WM_SETTINGCHANGE or Native.WM_DISPLAYCHANGE or Native.WM_DPICHANGED || (msg == taskbarCreated && msg != 0))
                Dispatcher.BeginInvoke(() => ScreensChanged(msg switch
                {
                    Native.WM_SETTINGCHANGE => "taskbar/work area",
                    Native.WM_DISPLAYCHANGE => "resolution",
                    Native.WM_DPICHANGED => "DPI",
                    _ => "Explorer restarted",
                }));
            // Monitors going to sleep or waking up (and the PC suspending or resuming): screens often get re-laid out
            // then, DisplayPort ones especially, without a display-settings event.
            else if (msg == Native.WM_POWERBROADCAST)
                Dispatcher.BeginInvoke(() => ScreensChanged("monitor power"));
            return IntPtr.Zero;
        });
        Native.WatchDisplayPower(hwnd);
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
        if (why == "DPI") CheckDpiFlicker();
    }

    readonly System.Collections.Generic.Queue<double> dpiChanges = new();
    double dpiEscapeUntil;

    /// <summary>
    /// Monitors with different scaling side by side: Windows gives his window the DPI of whichever monitor holds most
    /// of it. Straddling the join (standing, hovered mid-walk, climbing a window edge that sits on it), each rescale
    /// resizes and re-places the window so the larger share lands on the other monitor, which rescales it back, and he
    /// flashed between the two several times a second, indefinitely (2026-11-19). Four flips inside two seconds is no
    /// real monitor change: he lets go of whatever he's on and hops wholly onto the monitor he's on.
    /// </summary>
    void CheckDpiFlicker()
    {
        // The real clock, not `now`: the flood of DPI messages starves the frame timer, so `now` stops moving. Going by
        // it (and hopping, which only lands in a frame), the first escape of 2026-11-19 was its last: stuck for good.
        double t = clock.Elapsed.TotalSeconds;
        dpiChanges.Enqueue(t);
        while (dpiChanges.Count > 0 && t - dpiChanges.Peek() > 2) dpiChanges.Dequeue();
        if (dpiChanges.Count < 4 || t < dpiEscapeUntil || mode == Mode.Dragging) return;
        dpiChanges.Clear();
        dpiEscapeUntil = t + 1;

        var b = ScreenAt(x).Bounds;
        Native.GetWindowRect(hwnd, out var r);
        // Wholly on one monitor whatever scale he's drawn at: half the window at the larger of the two, and a bit.
        double half = RigCx * Math.Max(Math.Max(S, DrawnScale()), 1.5) + 8;
        double tx = Math.Clamp(x, b.Left + half, Math.Max(b.Left + half, b.Right - half));
        Store.Log($"DPI flicker: window at ({r.Left},{r.Top})-({r.Right},{r.Bottom}) straddles monitors with different scaling ({mode}, act {act}, climbing {climb != null}, perched {Perched}, x {x:0}, y {y:0}, drawn {DrawnScale():0.00}, dpi {S:0.00}); moved to {tx:0}.");
        // Put straight there, not hopped: a hop only lands in a frame, and frames are what the flicker starves.
        climb = null; peeking = false; perchHwnd = IntPtr.Zero;
        mode = Mode.Idle; nextWander = now + 10;
        x = tx; y = ScreenAt(x).WorkingArea.Bottom;
        Place(force: true);
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
    /// Screen that reads its working area, GetMonitorInfo's rcWork, on first use). Then the sanity check: if he or
    /// his window has strayed off the work area, he's put back at once.
    /// </summary>
    void RefreshScreens(string? why)
    {
        nextScreenCheck = now + (now < screenBurstUntil ? 0.25 : Math.Max(0.25, Store.Config.ScreenCheckSeconds));
        bool changed;
        try
        {
            var fresh = Native.MonitorRects()
                .Select(r => Forms.Screen.FromPoint(new System.Drawing.Point(r.Left + (r.Right - r.Left) / 2, r.Top + (r.Bottom - r.Top) / 2)))
                .ToArray();
            if (fresh.Length == 0) fresh = Forms.Screen.AllScreens;
            string key = string.Join(" | ", fresh.Select(s => $"{s.Bounds} work {s.WorkingArea}")) + $" dpi {S * 96:0}";
            screens = fresh;
            changed = key != screenKey;
            if (changed && (why != null || screenKey != "")) Store.Log($"Screens ({why ?? "check"}): {key}");
            screenKey = key;
        }
        catch (Exception e) { Store.Log($"Screens: couldn't re-read the monitors: {e.Message}"); return; }
        if (lastWinX == int.MinValue) return;   // not on screen yet: the start-up spot is clamped where it's set
        string reason = why ?? "safety check";
        bool moved = KeepOnWorkArea(reason);
        if (!changed && !moved && !WindowAdrift(reason)) return;
        Place(force: true);
        PlaceBubble(ScreenAt(x));
        Hurry();   // a few quick frames so the chat follows him straight away (VexPace.cs)
    }

    /// <summary>
    /// The one rule every state goes through, once a frame and after any monitor change: he stands on the work area
    /// (the monitor minus its taskbar) of the monitor he's on. His feet are never below its bottom and his body never
    /// off its sides. If he's somehow above its top (off screen), he's put down on its floor. Covers startup, walking,
    /// sitting, lunch and its nap, climbing, falls and drops, perching and hops alike. While you carry him he goes
    /// where you take him, except through the taskbar. Returns whether he had to be moved. Every correction is logged
    /// with its reason and where he was moved from and to, except you dragging him down into the floor.
    /// </summary>
    bool KeepOnWorkArea(string why)
    {
        var wa = ScreenAt(x).WorkingArea;
        if (mode == Mode.Dragging) { if (y > wa.Bottom) y = wa.Bottom; return false; }   // you pushing him down isn't a fault
        double ox = x, oy = y;
        if (mode == Mode.Climbing)
        {
            // The taskbar grew mid-climb: the way down now ends on the new floor, so he still finishes there.
            // His x belongs to the window side he's holding, so it's left alone.
            climbFromY = Math.Min(climbFromY, wa.Bottom);
            climbToY = Math.Min(climbToY, wa.Bottom);
        }
        else
        {
            double half = BodyHalfW * S, lo = wa.Left + half, hi = Math.Max(lo, wa.Right - half);
            if (mode == Mode.Walking)
            {
                // Walking on foot to the next monitor where the taskbars join (dodging, standing up from no clear
                // floor, going back after coding): the floor is both work areas, so he can cross the join. Clamped to
                // his own monitor alone, he was stopped at its edge each frame and the 0.3 s dodge check sent him off
                // again, flicking between walking and standing at the edge.
                var tw = ScreenAt(targetX).WorkingArea;
                if (tw != wa && !Perched && Walkable(x, targetX))
                {
                    lo = Math.Min(wa.Left, tw.Left) + half;
                    hi = Math.Max(lo, Math.Max(wa.Right, tw.Right) - half);
                }
                targetX = Math.Clamp(targetX, lo, hi);   // else he'd walk on the spot at the edge
            }
            x = Math.Clamp(x, lo, hi);
            if (y < wa.Top) { y = wa.Bottom; perchHwnd = IntPtr.Zero; }       // feet above the screen's top: wholly lost
        }
        if (y > wa.Bottom) y = wa.Bottom;
        if (Math.Abs(x - ox) < 0.5 && Math.Abs(y - oy) < 0.5) return false;
        Store.Log($"Floor: put back on the work area ({why}, {mode}): ({ox:0},{oy:0}) -> ({x:0},{y:0}), work area {wa}");
        return true;
    }

    /// <summary>
    /// Windows sometimes moves his window by itself (a Remote Desktop session or a DPI change re-laying out the
    /// screen). Place only moves it when his own position changes, so it could be left somewhere he isn't. True
    /// (and logged) if the window isn't where he last put it.
    /// </summary>
    bool WindowAdrift(string why)
    {
        if (!Native.GetWindowRect(hwnd, out var r) || (Math.Abs(r.Left - lastWinX) <= 1 && Math.Abs(r.Top - lastWinY) <= 1)) return false;
        Store.Log($"Floor: window found at ({r.Left},{r.Top}), should be at ({lastWinX},{lastWinY}) ({why}): put back.");
        return true;
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
