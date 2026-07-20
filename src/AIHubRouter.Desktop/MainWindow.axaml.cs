using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace AIHubRouter.Desktop;

public sealed partial class MainWindow : Window
{
    public MainWindow()
    {
        AvaloniaXamlLoader.Load(this);
    }
}
