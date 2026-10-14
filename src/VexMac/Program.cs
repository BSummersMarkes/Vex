using System;
using Avalonia;

namespace VexMac;

static class Program
{
    [STAThread]
    public static void Main(string[] args) =>
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args, Avalonia.Controls.ShutdownMode.OnExplicitShutdown);

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            // A little character, not an app you switch to: no Dock icon (Info.plist says the same with LSUIElement).
            .With(new MacOSPlatformOptions { ShowInDock = false })
            .LogToTrace();
}
