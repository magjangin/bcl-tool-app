using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Threading;
using BclToolApp.Models;
using BclToolApp.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BclToolApp.ViewModels;

public partial class GameDiscoveryViewModel : ObservableObject
{
    private readonly GameDiscoveryService _service = new();
    private readonly BclStrippingDetectorService _detector = new();
    private readonly AssemblyDiffViewModel _diff;
    private readonly LogAnalyzerViewModel _log;
    private readonly Action<int> _navigate;
    private InstalledGame[] _games = Array.Empty<InstalledGame>();

    /// <summary>Games found by the last scan, reused by the donor search instead of scanning Steam again.</summary>
    public IReadOnlyList<InstalledGame> Games => _games;

    public GameDiscoveryViewModel(AssemblyDiffViewModel diff, LogAnalyzerViewModel log, Action<int> navigate)
    {
        _diff = diff;
        _log = log;
        _navigate = navigate;
        _diff.KnownGames = () => _games;
    }

    [ObservableProperty] private string _searchRoot = string.Empty;
    [ObservableProperty] private string _searchQuery = string.Empty;
    [ObservableProperty] private ObservableCollection<InstalledGame> _filteredGames = new();
    [ObservableProperty] private InstalledGame? _selectedGame;
    [ObservableProperty] private string _status = "Steam 자동 검색 또는 게임/라이브러리 폴더 경로를 입력하세요.";
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private bool _onlySuspected = true;
    partial void OnOnlySuspectedChanged(bool value) => ApplyFilter();
    public bool IsIdle => !IsBusy;
    partial void OnIsBusyChanged(bool value) => OnPropertyChanged(nameof(IsIdle));
    partial void OnSearchQueryChanged(string value) => ApplyFilter();

    private void ApplyFilter()
    {
        var words = SearchQuery.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        FilteredGames = new ObservableCollection<InstalledGame>(_games.Where(g => (!OnlySuspected || g.Stripping.NeedsReview) && words.All(w =>
            g.Name.Contains(w, StringComparison.OrdinalIgnoreCase) || g.InstallPath.Contains(w, StringComparison.OrdinalIgnoreCase))).OrderBy(g => g.Stripping.Priority).ThenBy(g => g.Name));
        if (SelectedGame != null && !FilteredGames.Contains(SelectedGame)) SelectedGame = null;
    }

    [RelayCommand(IncludeCancelCommand = true)]
    private async Task DiscoverAsync(CancellationToken cancellationToken)
    {
        if (IsBusy) return;
        IsBusy = true;
        Status = "설치된 Unity 게임 검색 중… 이후 BCL 스트리핑을 자동 검사합니다.";
        var root = SearchRoot;
        try
        {
            var result = await Task.Run(() => _service.Discover(string.IsNullOrWhiteSpace(root)
                ? _service.FindSteamRoots() : new[] { root }, cancellationToken), cancellationToken);
            _games = result.Games.ToArray();
            SelectedGame = null;
            ApplyFilter();
            int completed = 0;
            foreach (var batch in _games.Chunk(12))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var reports = await Task.Run(() => batch.Select(g =>
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    return _detector.Inspect(g);
                }).ToArray(), cancellationToken);
                for (int i = 0; i < batch.Length; i++) batch[i].Stripping = reports[i];
                completed += batch.Length;
                ApplyFilter();
                Status = $"BCL 검사 {completed}/{_games.Length} · 스트리핑 의심 {_games.Count(g => g.Stripping.Status == BclStrippingStatus.Suspected)}개 · 파일 누락 {_games.Count(g => g.Stripping.Status == BclStrippingStatus.MissingFiles)}개";
            }
            Status = $"검사 완료 · Unity {_games.Length}개 / 스트리핑 의심 {_games.Count(g => g.Stripping.Status == BclStrippingStatus.Suspected)}개 / BCL 파일 누락 {_games.Count(g => g.Stripping.Status == BclStrippingStatus.MissingFiles)}개 / 판정 보류 {_games.Count(g => g.Stripping.Status == BclStrippingStatus.Inconclusive)}개" +
                (result.Warnings.Count > 0 ? $"\n검색 오류 {result.Warnings.Count}건: {string.Join(" / ", result.Warnings.Take(3))}" : "");
        }
        catch (OperationCanceledException) { Status = "검사를 취소했습니다. 완료된 결과는 유지합니다. 전체 보기에서 미검사 항목도 확인할 수 있습니다."; }
        catch (Exception ex) { Status = $"검색/검사 실패: {ex.Message}"; }
        finally { IsBusy = false; }
    }

    [RelayCommand]
    private async Task RecheckSelectedAsync()
    {
        if (IsBusy || SelectedGame == null) return;
        var game = SelectedGame;
        IsBusy = true;
        try
        {
            game.Stripping = await Task.Run(() => _detector.Inspect(game));
            Status = $"{game.Name}: {game.Stripping.Summary}";
            ApplyFilter();
        }
        catch (Exception ex) { Status = $"검사 실패: {ex.Message}"; }
        finally { IsBusy = false; }
    }
    [RelayCommand]
    private async Task UseAsTargetAsync()
    {
        if (!ValidateManaged()) return;
        _diff.GameManagedPath = SelectedGame!.ManagedPath;
        Status = $"대상 게임 지정: {SelectedGame.Name} · 같은 Unity LTS 줄의 도너를 찾는 중…";
        _navigate(2);
        await _diff.FindDonorCommand.ExecuteAsync(null);
        Status = $"대상 게임 지정: {SelectedGame.Name} · {_diff.DonorSearchStatus.Split('\n')[0]}";
    }

    [RelayCommand]
    private void UseAsDonor()
    {
        if (!ValidateManaged()) return;
        _diff.DonorBclPath = SelectedGame!.ManagedPath;
        Status = $"비교용 Donor 지정: {SelectedGame.Name}. 다른 게임의 DLL 호환성은 별도 확인이 필요합니다.";
        _navigate(2);
    }

    private bool ValidateManaged()
    {
        if (SelectedGame?.CanUseManaged != true || !Directory.Exists(SelectedGame.ManagedPath))
        {
            Status = "단일 Managed 폴더가 확인된 Mono 게임을 선택하세요. IL2CPP는 자동 이식 경로로 지정하지 않습니다.";
            return false;
        }
        return true;
    }

    [RelayCommand]
    private async Task LoadLogAsync()
    {
        if (IsBusy) return;
        var game = SelectedGame;
        if (game == null) { Status = "게임을 먼저 선택하세요."; return; }
        IsBusy = true;
        try
        {
            var path = await Task.Run(() => _service.InspectGame(game.InstallPath)?.LogPath);
            if (string.IsNullOrEmpty(path)) { Status = "MelonLoader/BepInEx 또는 게임 폴더의 로그가 없습니다. 게임을 실행한 뒤 다시 시도하세요."; return; }
            var content = await Task.Run(() => _service.ReadLogTail(path));
            _log.GameManagedPath = game.ManagedPath;
            _log.LogInputText = content;
            _log.AnalyzeLogCommand.Execute(null);
            Status = $"로그 불러옴 (마지막 최대 200,000자): {path}";
            _navigate(1);
        }
        catch (Exception ex) { Status = $"로그 읽기 실패: {ex.Message}"; }
        finally { IsBusy = false; }
    }

    [RelayCommand]
    private void OpenGameFolder()
    {
        try
        {
            if (SelectedGame == null || !Directory.Exists(SelectedGame.InstallPath)) { Status = "설치된 게임을 먼저 선택하세요."; return; }
            Process.Start(new ProcessStartInfo { FileName = SelectedGame.InstallPath, UseShellExecute = true });
        }
        catch (Exception ex) { Status = $"폴더 열기 실패: {ex.Message}"; }
    }
}


