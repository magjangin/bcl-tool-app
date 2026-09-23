using Avalonia.Controls;

namespace BclToolApp.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        Opened += async (_, _) =>
        {
            if (DataContext is BclToolApp.ViewModels.MainWindowViewModel vm)
                await vm.GameDiscovery.DiscoverCommand.ExecuteAsync(null);
        };
    }
}
