using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;

namespace AIHubRouter.Desktop;

public sealed partial class MainWindow : Window
{
    private bool _allowClose;
    private bool _closeDialogOpen;

    public MainWindow()
    {
        AvaloniaXamlLoader.Load(this);
    }

    private async void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        if (_allowClose ||
            e.CloseReason is WindowCloseReason.ApplicationShutdown or
                WindowCloseReason.OSShutdown or
                WindowCloseReason.OwnerWindowClosing)
        {
            return;
        }

        e.Cancel = true;
        if (_closeDialogOpen)
        {
            return;
        }

        _closeDialogOpen = true;
        try
        {
            var choice = await new CloseChoiceDialog().ShowDialog<CloseChoice>(this);
            switch (choice)
            {
                case CloseChoice.Minimize:
                    if (Application.Current is App app)
                    {
                        app.MinimizeToTray(this);
                    }
                    else
                    {
                        Hide();
                    }
                    break;
                case CloseChoice.Exit:
                    _allowClose = true;
                    if (Application.Current?.ApplicationLifetime is
                        IClassicDesktopStyleApplicationLifetime desktop)
                    {
                        desktop.Shutdown();
                    }
                    else
                    {
                        Close();
                    }
                    break;
            }
        }
        finally
        {
            _closeDialogOpen = false;
        }
    }
}
