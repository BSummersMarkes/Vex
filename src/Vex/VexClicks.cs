using System;
using System.Linq;
using System.Windows;
using System.Windows.Interop;

namespace Vex;

/// <summary>
/// Keeping him clickable. On 23 Oct 2026 clicks on him stopped doing anything while the chat (opened from the tray)
/// still worked. Nothing in the floor fixes touches his window styles, so the likeliest causes were checked instead:
///   - Setup is a modal dialog, and while one is open Windows disables every other window of Vex's, him included,
///     until it closes. Left open behind other windows, it made him look dead to clicks (the tray's chat is a new
///     window, so that still opened). Clicking him now brings the open dialog forward, the way Windows does for a
///     dialog's owner, so it's clear why he isn't answering;
///   - a hop that finished while he was vanished for a full-screen app made him visible again without making him
///     hit-testable (fixed where the hop ends, and checked here);
///   - something else stealing the top spot where he's drawn, or a window style that makes clicks pass through him.
/// Every ClickCheckSeconds this puts back his styles (tool window, no-activate, topmost, never click-through),
/// re-enables his window if it's been left disabled with no dialog open, and asks Windows which window a click on
/// the middle of his body would go to. Anything wrong is fixed and written to vex.log and crash.log (vex.log only in
/// Vex Lite), once per incident.
/// </summary>
public partial class VexWindow
{
    double nextClickCheck;
    /// <summary>Class name of another app's window found over him at the last check, or null if clicks reached him.</summary>
    string? blockedBy;
    bool blockedLogged, dialogNoted;

    /// <summary>Called once the window exists: a click on him while a dialog has him disabled brings the dialog forward.</summary>
    void WatchClicks()
    {
        HwndSource.FromHwnd(hwnd)?.AddHook((IntPtr h, int msg, IntPtr w, IntPtr l, ref bool handled) =>
        {
            // A disabled window gets no clicks, only WM_SETCURSOR with HTERROR and the button message that caused it.
            // Only his drawn pixels get it at all (the see-through ones belong to whatever is underneath).
            long lp = l.ToInt64();
            if (msg == Native.WM_SETCURSOR && (short)(lp & 0xFFFF) == Native.HTERROR && ((lp >> 16) & 0xFFFF) == Native.WM_LBUTTONDOWN)
                Dispatcher.BeginInvoke(() => BringDialogForward());
            return IntPtr.Zero;
        });
    }

    /// <summary>He was clicked while a dialog (Setup, usually) has him disabled: show that dialog rather than nothing.</summary>
    void BringDialogForward()
    {
        var dlg = Application.Current.Windows.OfType<Window>().LastOrDefault(w =>
            w != this && w != bubbleWin && w != chat && w != daemon && w.IsVisible && Native.IsWindowEnabled(new WindowInteropHelper(w).Handle));
        if (dlg == null) return;
        Store.Log($"Clicks: clicked while \"{dlg.Title}\" was open: brought it forward.");
        Native.BringToFront(new WindowInteropHelper(dlg).Handle);
    }

    /// <summary>Called every frame: on the ClickCheckSeconds timer, makes sure a click on him still reaches him.</summary>
    void KeepClickable()
    {
        if (now < nextClickCheck) return;
        nextClickCheck = now + Math.Max(0.5, Store.Config.ClickCheckSeconds);
        try
        {
            CheckStyles();
            if (!CheckEnabled()) return;   // a disabled window can't be hit-tested; the dialog is the reason
            CheckVisibility();
            CheckHitTest();
        }
        catch (Exception e) { Store.Log($"Clicks: check failed: {e.Message}"); }
    }

    /// <summary>Tool window and no-activate as set at start, topmost, and never click-through as a whole.</summary>
    void CheckStyles()
    {
        long ex = Native.GetWindowLongPtr(hwnd, Native.GWL_EXSTYLE);
        long want = (ex | Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE) & ~Native.WS_EX_TRANSPARENT;
        if (want != ex)
        {
            Native.SetWindowLongPtr(hwnd, Native.GWL_EXSTYLE, want);
            ClickLog($"Clicks: window style was 0x{ex:X}, put back to 0x{want:X}.");
        }
        if ((ex & Native.WS_EX_TOPMOST) == 0)
        {
            ReclaimTop();
            ClickLog("Clicks: window had lost topmost: put back on top.");
        }
    }

    /// <summary>
    /// False while his window is disabled because a dialog is open (that's Windows' rule; clicks bring the dialog
    /// forward). Disabled with no dialog open, it was left that way by mistake, so it's enabled again.
    /// </summary>
    bool CheckEnabled()
    {
        if (Native.IsWindowEnabled(hwnd)) { dialogNoted = false; return true; }
        if (System.Windows.Interop.ComponentDispatcher.IsThreadModal)
        {
            if (!dialogNoted) Store.Log("Clicks: a dialog is open, so Windows has him disabled until it closes; clicking him brings it forward.");
            dialogNoted = true;
            return false;
        }
        Native.EnableWindow(hwnd, true);
        ClickLog("Clicks: window was disabled with no dialog open: enabled again.");
        return true;
    }

    /// <summary>Shown (not hidden, not vanished for full screen, not mid-hop) means fully opaque and hit-testable.</summary>
    void CheckVisibility()
    {
        if (mode == Mode.Hopping) return;   // fading out and in on purpose
        bool gone = hidden || autoHidden;
        if (gone ? !IsHitTestVisible && Opacity == 0 : IsHitTestVisible && Opacity >= 1) return;
        ClickLog($"Clicks: {(gone ? "hidden" : "shown")} but opacity {Opacity:0.##} and hit-testable {IsHitTestVisible}: put right.");
        ApplyVisibility();
    }

    /// <summary>
    /// Asks Windows which window a click on the middle of his body would go to. If it's another app's (something else
    /// grabbed the top spot), he takes the top back; if that hasn't helped by the next check, it's logged once.
    /// His own windows over him (the chat, a bubble, Setup) are fine.
    /// </summary>
    void CheckHitTest()
    {
        if (hidden || autoHidden || mode is Mode.Dragging or Mode.Hopping || pressed) { blockedBy = null; return; }
        var p = new Point(60, 100);   // middle of his torso, in the Rig's own drawing coordinates
        if (Rig.InputHitTest(p) == null) return;   // this pose leaves nothing of him there; try again next time
        var s = Rig.PointToScreen(p);   // physical pixels, through every transform he's posed with
        var top = Native.TopWindowAt((int)Math.Round(s.X), (int)Math.Round(s.Y));
        if (top == hwnd || top == IntPtr.Zero) { blockedBy = null; return; }
        Native.GetWindowThreadProcessId(top, out uint pid);
        if (pid == ownPid) { blockedBy = null; return; }
        string who = Native.ClassName(top);
        if (blockedBy == who && !blockedLogged)
        {
            blockedLogged = true;
            ClickLog($"Clicks: a click on him at ({s.X:0},{s.Y:0}) goes to another app's window ({who}) even after taking the top back ({mode}).");
        }
        else if (blockedBy != who) { blockedBy = who; blockedLogged = false; }
        ReclaimTop();
    }

    void ReclaimTop() => Native.SetWindowPos(hwnd, Native.HWND_TOPMOST, 0, 0, 0, 0, Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);

    /// <summary>Something that was stopping clicks reaching him: into vex.log, and crash.log in the full Vex.</summary>
    static void ClickLog(string text)
    {
        Store.Log(text);
#if !LITE
        Services.Lifeline.CrashLog(text);
#endif
    }
}
