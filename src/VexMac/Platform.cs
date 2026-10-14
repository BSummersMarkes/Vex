using System;
using System.IO;
using System.Runtime.InteropServices;

namespace VexMac;

/// <summary>The few things that differ between the Mac and Windows (Windows is only for trying it out).</summary>
static class Platform
{
    /// <summary>~/Library/Application Support/Vex on a Mac; %LOCALAPPDATA%\Vex Mac when trying it on Windows.</summary>
    public static string DataDir { get; } = Environment.GetEnvironmentVariable("VEX_DATA_DIR") is { Length: > 0 } d ? d
        : OperatingSystem.IsMacOS()
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "Application Support", "Vex")
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Vex Mac");

    /// <summary>Seconds since the last keyboard or mouse input anywhere. 0 if it can't be read.</summary>
    public static double IdleSeconds()
    {
        try
        {
            if (OperatingSystem.IsMacOS())
                return CGEventSourceSecondsSinceLastEventType(1 /* HID system state */, uint.MaxValue /* any input */);
            if (OperatingSystem.IsWindows())
            {
                var info = new LASTINPUTINFO { cbSize = (uint)Marshal.SizeOf<LASTINPUTINFO>() };
                return GetLastInputInfo(ref info) ? (uint)(Environment.TickCount - (int)info.dwTime) / 1000.0 : 0;
            }
        }
        catch { }
        return 0;
    }

    [DllImport("/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics")]
    static extern double CGEventSourceSecondsSinceLastEventType(int stateId, uint eventType);

    [StructLayout(LayoutKind.Sequential)]
    struct LASTINPUTINFO { public uint cbSize; public uint dwTime; }
    [DllImport("user32.dll")]
    static extern bool GetLastInputInfo(ref LASTINPUTINFO info);

    // ───────────── start at login (Mac: a LaunchAgent in ~/Library/LaunchAgents) ─────────────

    static string AgentPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                                            "Library", "LaunchAgents", "com.vex.companion.plist");

    public static bool CanStartAtLogin => OperatingSystem.IsMacOS();
    public static bool StartsAtLogin => CanStartAtLogin && File.Exists(AgentPath);

    public static void SetStartAtLogin(bool on)
    {
        if (!CanStartAtLogin) return;
        try
        {
            if (!on) { if (File.Exists(AgentPath)) File.Delete(AgentPath); return; }
            // Vex.app itself (the executable lives inside it, in Contents/MacOS), opened the way Finder would.
            var exe = Environment.ProcessPath ?? "";
            int i = exe.IndexOf(".app/", StringComparison.Ordinal);
            var app = i >= 0 ? exe[..(i + 4)] : exe;
            Directory.CreateDirectory(Path.GetDirectoryName(AgentPath)!);
            File.WriteAllText(AgentPath,
                "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n" +
                "<!DOCTYPE plist PUBLIC \"-//Apple//DTD PLIST 1.0//EN\" \"http://www.apple.com/DTDs/PropertyList-1.0.dtd\">\n" +
                "<plist version=\"1.0\">\n<dict>\n" +
                "    <key>Label</key><string>com.vex.companion</string>\n" +
                $"    <key>ProgramArguments</key><array><string>/usr/bin/open</string><string>{System.Security.SecurityElement.Escape(app)}</string></array>\n" +
                "    <key>RunAtLoad</key><true/>\n" +
                "</dict>\n</plist>\n");
        }
        catch (Exception ex) { State.Log($"Start at login: {ex.Message}"); }
    }
}
