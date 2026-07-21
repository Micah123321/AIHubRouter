using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;

namespace AIHubRouter.Desktop;

public enum CloseChoice
{
    Cancel,
    Minimize,
    Exit
}

public sealed partial class CloseChoiceDialog : Window
{
    public CloseChoiceDialog()
    {
        AvaloniaXamlLoader.Load(this);
    }

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(CloseChoice.Cancel);

    private void OnMinimize(object? sender, RoutedEventArgs e) => Close(CloseChoice.Minimize);

    private void OnExit(object? sender, RoutedEventArgs e) => Close(CloseChoice.Exit);
}
