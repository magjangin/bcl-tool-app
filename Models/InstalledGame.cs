using CommunityToolkit.Mvvm.ComponentModel;

namespace BclToolApp.Models;

public sealed partial class InstalledGame : ObservableObject
{
    [ObservableProperty]
    private BclStrippingResult _stripping = new();

    public string Name { get; init; } = string.Empty;
    public string InstallPath { get; init; } = string.Empty;
    public string ManagedPath { get; init; } = string.Empty;
    public string Runtime { get; init; } = "알 수 없음";
    public string Loader { get; init; } = "미검출";
    public string LogPath { get; init; } = string.Empty;
    public bool CanUseManaged => Runtime == "Mono" && !string.IsNullOrEmpty(ManagedPath);
    public string Details => $"{Runtime} · 로더: {Loader}";
}

