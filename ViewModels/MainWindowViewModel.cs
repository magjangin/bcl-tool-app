using CommunityToolkit.Mvvm.ComponentModel;

namespace BclToolApp.ViewModels;

public partial class MainWindowViewModel : ViewModelBase
{
    [ObservableProperty]
    private LogAnalyzerViewModel _logAnalyzer = new();

    [ObservableProperty]
    private AssemblyDiffViewModel _assemblyDiff = new();

    [ObservableProperty]
    private TransplantViewModel _transplant;

    [ObservableProperty]
    private int _selectedTabIndex;

    public GameDiscoveryViewModel GameDiscovery { get; }

    public MainWindowViewModel()
    {
        _transplant = new TransplantViewModel(_assemblyDiff);
        GameDiscovery = new GameDiscoveryViewModel(_assemblyDiff, _logAnalyzer, index => SelectedTabIndex = index);
    }
}

