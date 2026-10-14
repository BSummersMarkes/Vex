using System;
using System.IO;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Media.Imaging;

namespace VexMac;

public partial class App : Application
{
    VexWindow? vex;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime)
        {
            vex = new VexWindow();
            vex.Show();
            MakeMenuBarIcon();
        }
        base.OnFrameworkInitializationCompleted();
    }

    public static void Quit()
    {
        State.Current.Save();
        (Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.Shutdown();
    }

    /// <summary>
    /// His icon in the menu bar (the Windows tray, when trying it there): with no Dock icon, it's where Quit lives,
    /// along with the same few things as right-clicking him.
    /// </summary>
    void MakeMenuBarIcon()
    {
        var menu = new NativeMenu();
        NativeMenuItem Item(string header, Action act) { var m = new NativeMenuItem(header); m.Click += (_, _) => act(); return m; }
        menu.Add(Item("Taking a break", () => vex?.TakingBreak()));
        menu.Add(Item("I refilled my coffee", () => vex?.Refill()));
        if (Platform.CanStartAtLogin)
        {
            var login = new NativeMenuItem("Start at login") { ToggleType = NativeMenuItemToggleType.CheckBox, IsChecked = Platform.StartsAtLogin };
            login.Click += (_, _) => { Platform.SetStartAtLogin(!Platform.StartsAtLogin); login.IsChecked = Platform.StartsAtLogin; };
            menu.Add(login);
        }
        menu.Add(new NativeMenuItemSeparator());
        menu.Add(Item("Quit Vex", Quit));

        var tray = new TrayIcon { Icon = MakeIcon(), ToolTipText = "Vex", Menu = menu, IsVisible = true };
        TrayIcon.SetIcons(this, new TrayIcons { tray });
    }

    /// <summary>A tiny hood with two eyes, drawn at runtime so there's no icon file to maintain.</summary>
    static WindowIcon MakeIcon()
    {
        var bmp = new RenderTargetBitmap(new PixelSize(32, 32));
        using (var dc = bmp.CreateDrawingContext())
        {
            dc.DrawEllipse(new SolidColorBrush(Color.Parse("#82292F")), null, new Point(16, 16), 15, 15);
            dc.DrawEllipse(Brushes.Black, null, new Point(16, 17), 10, 9);
            dc.DrawEllipse(Brushes.White, null, new Point(13, 17), 2, 3);
            dc.DrawEllipse(Brushes.White, null, new Point(20, 17), 2, 3);
        }
        var ms = new MemoryStream();
        bmp.Save(ms);
        ms.Position = 0;
        return new WindowIcon(ms);
    }
}
