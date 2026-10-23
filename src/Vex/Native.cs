using System;
using System.Runtime.InteropServices;
using System.Text;

namespace Vex;

static class Native
{
    /// <summary>This process's id, to tell Vex's own windows from everyone else's.</summary>
    public static readonly uint OwnPid = (uint)System.Diagnostics.Process.GetCurrentProcess().Id;

    /// <summary>The running exe, for the start-with-Windows entry.</summary>
#if LITE
    public static string ExePath => System.Diagnostics.Process.GetCurrentProcess().MainModule!.FileName;
#else
    public static string ExePath => Environment.ProcessPath!;
#endif

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT { public int X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    struct LASTINPUTINFO { public uint cbSize; public uint dwTime; }

    public static readonly IntPtr HWND_TOPMOST = new(-1);
    public const uint SWP_NOSIZE = 0x0001, SWP_NOMOVE = 0x0002, SWP_NOACTIVATE = 0x0010;
    public const int GWL_EXSTYLE = -20;
    public const long WS_EX_TOOLWINDOW = 0x80, WS_EX_NOACTIVATE = 0x08000000;
    /// <summary>Mouse clicks go straight through the whole window (with layered windows), not just its see-through pixels.</summary>
    public const long WS_EX_TRANSPARENT = 0x20;
    public const long WS_EX_TOPMOST = 0x08;
    public const int WM_SETCURSOR = 0x0020, WM_LBUTTONDOWN = 0x0201, HTERROR = -2;
    const uint GA_ROOT = 2;
    [DllImport("user32.dll")] public static extern bool IsWindowEnabled(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool EnableWindow(IntPtr hWnd, bool enable);
    [DllImport("user32.dll")] static extern IntPtr WindowFromPoint(POINT p);
    [DllImport("user32.dll")] static extern IntPtr GetAncestor(IntPtr hWnd, uint flags);

    /// <summary>The top-level window a click at this screen point (physical pixels) would go to, or zero.</summary>
    public static IntPtr TopWindowAt(int x, int y)
    {
        var h = WindowFromPoint(new POINT { X = x, Y = y });
        return h == IntPtr.Zero ? h : GetAncestor(h, GA_ROOT);
    }
    const int DWMWA_EXTENDED_FRAME_BOUNDS = 9, DWMWA_CLOAKED = 14;

    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hWnd, out RECT r);
    [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool GetCursorPos(out POINT p);
    [DllImport("user32.dll")] public static extern uint GetDpiForWindow(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr hWnd, StringBuilder s, int n);
    [DllImport("user32.dll")] static extern bool GetLastInputInfo(ref LASTINPUTINFO lii);
    [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] public static extern long GetWindowLongPtr(IntPtr hWnd, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] public static extern long SetWindowLongPtr(IntPtr hWnd, int index, long value);
    [DllImport("dwmapi.dll")] static extern int DwmGetWindowAttribute(IntPtr hWnd, int attr, out RECT r, int size);
    [DllImport("dwmapi.dll")] static extern int DwmGetWindowAttribute(IntPtr hWnd, int attr, out int v, int size);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowText(IntPtr hWnd, StringBuilder s, int n);
    [DllImport("user32.dll")] public static extern bool SetWindowDisplayAffinity(IntPtr hWnd, uint affinity);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hWnd, int cmd);
    delegate bool EnumProc(IntPtr hWnd, IntPtr lParam);
    [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc cb, IntPtr lParam);

    public const uint WDA_EXCLUDEFROMCAPTURE = 0x11;
    public const int SW_RESTORE = 9;

    [DllImport("user32.dll")] static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool attach);
    [DllImport("user32.dll")] static extern bool BringWindowToTop(IntPtr hWnd);
    [DllImport("kernel32.dll")] static extern uint GetCurrentThreadId();

    /// <summary>
    /// Restores a window if it's minimised and brings it to the front with the keyboard focus. Vex's windows never
    /// take focus, so Windows' foreground lock can refuse a plain SetForegroundWindow (the taskbar button just
    /// flashes); if it does, Vex briefly joins the input of whoever is in front, which lets the switch through.
    /// Returns whether the window really ended up in front.
    /// </summary>
    public static bool BringToFront(IntPtr hWnd)
    {
        if (!IsWindow(hWnd)) return false;
        if (IsIconic(hWnd)) ShowWindow(hWnd, SW_RESTORE);
        if (SetForegroundWindow(hWnd) && GetForegroundWindow() == hWnd) return true;
        uint me = GetCurrentThreadId(), front = GetWindowThreadProcessId(GetForegroundWindow(), out _);
        bool joined = front != 0 && front != me && AttachThreadInput(me, front, true);
        try { BringWindowToTop(hWnd); SetForegroundWindow(hWnd); }
        finally { if (joined) AttachThreadInput(me, front, false); }
        return GetForegroundWindow() == hWnd;
    }

    public static string WindowText(IntPtr hWnd)
    {
        var sb = new StringBuilder(512);
        GetWindowText(hWnd, sb, sb.Capacity);
        return sb.ToString();
    }

    /// <summary>Visible top-level windows belonging to the given process ids.</summary>
    public static System.Collections.Generic.List<IntPtr> WindowsOf(System.Collections.Generic.ISet<uint> pids)
    {
        var found = new System.Collections.Generic.List<IntPtr>();
        EnumWindows((h, _) =>
        {
            GetWindowThreadProcessId(h, out uint pid);
            if (pids.Contains(pid) && IsWindowVisible(h) && WindowText(h).Length > 0) found.Add(h);
            return true;
        }, IntPtr.Zero);
        return found;
    }

    [DllImport("user32.dll")] public static extern bool IsZoomed(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool IsWindow(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool RegisterHotKey(IntPtr hWnd, int id, uint mods, uint vk);
    public const uint MOD_ALT = 1, MOD_CONTROL = 2, MOD_SHIFT = 4, MOD_WIN = 8, MOD_NOREPEAT = 0x4000;
    public const int WM_HOTKEY = 0x0312;

    [DllImport("user32.dll")] static extern int GetWindowTextLength(IntPtr hWnd);

    delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdc, ref RECT r, IntPtr lParam);
    [DllImport("user32.dll")] static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip, MonitorEnumProc cb, IntPtr lParam);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern uint RegisterWindowMessage(string name);
    public const int WM_SETTINGCHANGE = 0x001A, WM_DISPLAYCHANGE = 0x007E, WM_DPICHANGED = 0x02E0;

    /// <summary>Each monitor's full bounds as Windows has them right now (physical pixels), asked fresh every call.</summary>
    public static System.Collections.Generic.List<RECT> MonitorRects()
    {
        var list = new System.Collections.Generic.List<RECT>();
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr _, IntPtr _, ref RECT r, IntPtr _) => { list.Add(r); return true; }, IntPtr.Zero);
        return list;
    }

    [DllImport("user32.dll")] static extern IntPtr RegisterPowerSettingNotification(IntPtr hRecipient, ref Guid setting, int flags);
    public const int WM_POWERBROADCAST = 0x0218;

    /// <summary>
    /// Asks Windows to send the window WM_POWERBROADCAST when the monitors turn off, dim or come back on
    /// (GUID_CONSOLE_DISPLAY_STATE), on top of the usual suspend and resume ones.
    /// </summary>
    public static void WatchDisplayPower(IntPtr hWnd)
    {
        var consoleDisplayState = new Guid("6FE69556-704A-47A0-8F24-C28D936FDA47");
        RegisterPowerSettingNotification(hWnd, ref consoleDisplayState, 0 /* DEVICE_NOTIFY_WINDOW_HANDLE */);
    }

    /// <summary>
    /// Visible, titled top-level windows of other processes, topmost first, with their visible bounds. Used for perching
    /// and climbing, which only need where windows are: whether one has a title is checked (untitled ones are mostly
    /// invisible helpers), but the title itself isn't read.
    /// </summary>
    public static System.Collections.Generic.List<(IntPtr Hwnd, RECT Rect)> TopWindows(uint exceptPid)
    {
        var list = new System.Collections.Generic.List<(IntPtr, RECT)>();
        EnumWindows((h, _) =>
        {
            if (!IsWindowVisible(h) || IsIconic(h) || IsCloaked(h)) return true;
            GetWindowThreadProcessId(h, out uint pid);
            if (pid == exceptPid || GetWindowTextLength(h) == 0) return true;
            var r = Bounds(h);
            if (r.Right - r.Left > 50 && r.Bottom - r.Top > 50) list.Add((h, r));
            return true;
        }, IntPtr.Zero);
        return list;
    }

    public static string ClassName(IntPtr hWnd)
    {
        var sb = new StringBuilder(256);
        GetClassName(hWnd, sb, sb.Capacity);
        return sb.ToString();
    }

    /// <summary>Visible bounds of a window (without the invisible resize border).</summary>
    public static RECT Bounds(IntPtr hWnd)
    {
        if (DwmGetWindowAttribute(hWnd, DWMWA_EXTENDED_FRAME_BOUNDS, out RECT r, Marshal.SizeOf<RECT>()) == 0) return r;
        GetWindowRect(hWnd, out r);
        return r;
    }

    public static bool IsCloaked(IntPtr hWnd) =>
        DwmGetWindowAttribute(hWnd, DWMWA_CLOAKED, out int v, sizeof(int)) == 0 && v != 0;

    /// <summary>Time since the last keyboard or mouse input anywhere in the session.</summary>
    public static TimeSpan IdleTime()
    {
        var lii = new LASTINPUTINFO { cbSize = (uint)Marshal.SizeOf<LASTINPUTINFO>() };
        if (!GetLastInputInfo(ref lii)) return TimeSpan.Zero;
        return TimeSpan.FromMilliseconds(unchecked((uint)Environment.TickCount - lii.dwTime));
    }
}
