using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;

namespace DeckRemote;

internal sealed class App : Application
{
    public override void Initialize()
    {
        RequestedThemeVariant = ThemeVariant.Dark;
        Styles.Add(new FluentTheme());
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var window = new MainWindow();
            desktop.MainWindow = window;
            if (OperatingSystem.IsWindows())
            {
                using var iconData = AssetLoader.Open(new Uri("avares://DeckRemote/Assets/deck-remote.png"));
                var icon = new WindowIcon(iconData);
                window.Icon = icon;
                var show = new NativeMenuItem("打开 Deck Remote");
                var exit = new NativeMenuItem("退出");
                var menu = new NativeMenu();
                menu.Items.Add(show);
                menu.Items.Add(exit);
                var tray = new TrayIcon { ToolTipText = "Deck Remote", Menu = menu, Icon = icon, IsVisible = true };
                TrayIcon.SetIcons(this, new TrayIcons { tray });
                show.Click += (_, _) => window.Present();
                tray.Clicked += (_, _) => window.Present();
                exit.Click += async (_, _) =>
                {
                    await window.StopAsync();
                    tray.Dispose();
                    desktop.Shutdown();
                };
                desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
                if (Program.TrayLaunch)
                {
                    window.Opened += (_, _) => window.Hide();
                }
            }
            desktop.Exit += (_, _) => window.StopAsync().GetAwaiter().GetResult();
        }
        base.OnFrameworkInitializationCompleted();
    }
}
