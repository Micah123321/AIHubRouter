using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;

namespace AIHubRouter.Desktop;

public sealed partial class App : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            var viewModel = new MainWindowViewModel();
            var window = new MainWindow { DataContext = viewModel };
            window.Closed += (_, _) => viewModel.Dispose();
            desktop.MainWindow = window;
        }

        base.OnFrameworkInitializationCompleted();
    }

    public void MinimizeToTray(MainWindow window)
    {
        window.Hide();
        SetTrayIconVisible(true);
    }

    private void OnTrayIconClicked(object? sender, EventArgs e) => ShowMainWindow();

    private void OnOpenMainWindow(object? sender, EventArgs e) => ShowMainWindow();

    private void OnExitFromTray(object? sender, EventArgs e)
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.Shutdown();
        }
    }

    private void ShowMainWindow()
    {
        if (ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop ||
            desktop.MainWindow is not { } window)
        {
            return;
        }

        SetTrayIconVisible(false);
        window.Show();
        if (window.WindowState == WindowState.Minimized)
        {
            window.WindowState = WindowState.Normal;
        }

        window.Activate();
    }

    private void SetTrayIconVisible(bool visible)
    {
        if (TrayIcon.GetIcons(this) is { Count: > 0 } icons)
        {
            icons[0].IsVisible = visible;
        }
    }
}
