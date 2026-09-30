using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using BclToolApp.Models;
using BclToolApp.Services;

namespace BclToolApp.ViewModels;

public partial class AssemblyDiffViewModel : ObservableObject
{
    private readonly BclAssemblyInspectorService _inspectorService = new();
    private readonly DonorSearchService _donorSearchService = new();
    private readonly GameDiscoveryService _discoveryService = new();

    /// <summary>Games already scanned in the detection tab. Without them the donor search scans Steam itself.</summary>
    public Func<IReadOnlyList<InstalledGame>>? KnownGames { get; set; }

    /// <summary>Also offer Unity editor Mono profiles as donors.</summary>
    public bool SearchUnityEditors { get; set; } = true;

    [ObservableProperty]
    private string _gameManagedPath = string.Empty;

    [ObservableProperty]
    private string _donorBclPath = string.Empty;

    [ObservableProperty]
    private ObservableCollection<AssemblyDiffItem> _allDiffItems = new();

    [ObservableProperty]
    private ObservableCollection<AssemblyDiffItem> _filteredDiffItems = new();

    [ObservableProperty]
    private bool _filterOnlyMissing;

    /// <summary>Default on: a donor game ships hundreds of its own DLLs that are never transplant material.</summary>
    [ObservableProperty]
    private bool _filterOnlyBcl = true;

    [ObservableProperty]
    private string _statusSummary = "비교할 게임 및 Donor BCL 디렉토리를 지정하세요.";

    [ObservableProperty]
    private string _symbolSearchQuery = "System.Linq.Enumerable.Select";

    [ObservableProperty]
    private string _symbolSearchResult = string.Empty;

    [ObservableProperty]
    private AssemblyDiffItem? _selectedItem;

    [ObservableProperty]
    private ObservableCollection<DonorCandidate> _donorCandidates = new();

    [ObservableProperty]
    private DonorCandidate? _selectedDonorCandidate;

    [ObservableProperty]
    private string _donorSearchStatus = "대상 게임을 지정한 뒤 [도너 자동 찾기]를 누르면 같은 Unity LTS 줄의 온전한 BCL을 찾습니다.";

    [ObservableProperty]
    private bool _isSearchingDonor;

    public bool IsNotSearchingDonor => !IsSearchingDonor;
    partial void OnIsSearchingDonorChanged(bool value) => OnPropertyChanged(nameof(IsNotSearchingDonor));

    public bool HasDonorCandidates => DonorCandidates.Count > 0;
    partial void OnDonorCandidatesChanged(ObservableCollection<DonorCandidate> value) => OnPropertyChanged(nameof(HasDonorCandidates));

    partial void OnSelectedDonorCandidateChanged(DonorCandidate? value)
    {
        if (value != null) DonorBclPath = value.BclPath;
    }

    [RelayCommand(IncludeCancelCommand = true)]
    private async Task FindDonorAsync(CancellationToken cancellationToken)
    {
        if (IsSearchingDonor) return;
        var (resolved, note) = GameDiscoveryService.ResolveBclDirectory(GameManagedPath);
        if (resolved == null)
        {
            DonorSearchStatus = $"대상 게임 경로를 확인하세요. {note}";
            return;
        }
        if (!string.Equals(resolved, GameManagedPath, StringComparison.OrdinalIgnoreCase)) GameManagedPath = resolved;
        var managedPath = resolved;
        IsSearchingDonor = true;
        SelectedDonorCandidate = null;
        DonorCandidates = new ObservableCollection<DonorCandidate>();
        try
        {
            var known = KnownGames?.Invoke() ?? Array.Empty<InstalledGame>();
            DonorSearchStatus = known.Count > 0
                ? $"검사한 게임 {known.Count}개에서 도너 후보를 찾는 중…"
                : "설치된 게임을 검색하는 중… ([스트리핑 게임 감지] 탭을 먼저 실행하면 더 빠릅니다)";
            var result = await Task.Run(() => _donorSearchService.Find(managedPath,
                known.Count > 0 ? known : _discoveryService.Discover(_discoveryService.FindSteamRoots(), cancellationToken).Games,
                SearchUnityEditors, cancellationToken), cancellationToken);

            DonorCandidates = new ObservableCollection<DonorCandidate>(result.Candidates);
            var notes = result.Notes.Count > 0 ? "\n" + string.Join("\n", result.Notes) : string.Empty;
            if (result.Candidates.Count == 0)
            {
                DonorSearchStatus = $"조건에 맞는 도너를 찾지 못했습니다. 대상: {result.Target.Display}{notes}";
            }
            else if (result.AutoApply is { } best)
            {
                SelectedDonorCandidate = best;
                DonorSearchStatus = $"도너 자동 지정: {best.Name} ({best.Reason}) · 후보 {result.Candidates.Count}개\n대상: {result.Target.Display}{notes}";
                // Finish the job: compare against the donor and check the files that need replacing.
                Scan();
                if (AllDiffItems.Count > 0) SelectRecommended();
            }
            else
            {
                DonorSearchStatus = $"자동 적용할 만한 도너가 없습니다. 아래 후보를 직접 확인하고 선택하세요. 대상: {result.Target.Display}{notes}";
            }
        }
        catch (OperationCanceledException) { DonorSearchStatus = "도너 검색을 취소했습니다."; }
        catch (Exception ex) { DonorSearchStatus = $"도너 검색 실패: {ex.Message}"; }
        finally { IsSearchingDonor = false; }
    }

    [RelayCommand]
    private void Scan()
    {
        InvalidateScan();
        if (!ResolvePaths(out var error))
        {
            StatusSummary = error;
            return;
        }

        try
        {
            var items = _inspectorService.CompareDirectories(GameManagedPath, DonorBclPath);
            AllDiffItems = new ObservableCollection<AssemblyDiffItem>(items);
            ApplyFilter();

            int missingCount = items.Count(x => x.Status == AssemblyDiffStatus.MissingInGame);
            int mismatchCount = items.Count(x => x.Status == AssemblyDiffStatus.VersionMismatch);
            StatusSummary = $"총 {items.Count}개 어셈블리 검사 완료 (누락: {missingCount}개, 버전불일치: {mismatchCount}개, 내용차이: {items.Count(x => x.Status == AssemblyDiffStatus.ContentMismatch)}개, 분석불가: {items.Count(x => x.Status == AssemblyDiffStatus.InvalidAssembly)}개)";
        }
        catch (Exception ex)
        {
            StatusSummary = $"비교 실패: {ex.Message}";
        }
    }

    /// <summary>Rewrites both fields to the folder that actually holds the BCL, so pasting a game folder works.</summary>
    private bool ResolvePaths(out string error)
    {
        var (game, gameNote) = GameDiscoveryService.ResolveBclDirectory(GameManagedPath);
        if (game == null) { error = $"게임 Managed 폴더: {gameNote}"; return false; }
        if (!string.Equals(game, GameManagedPath, StringComparison.OrdinalIgnoreCase)) GameManagedPath = game;

        var (donor, donorNote) = GameDiscoveryService.ResolveBclDirectory(DonorBclPath);
        if (donor == null) { error = $"Donor BCL 폴더: {donorNote}"; return false; }
        if (!string.Equals(donor, DonorBclPath, StringComparison.OrdinalIgnoreCase)) DonorBclPath = donor;
        error = string.Empty;
        return true;
    }

    /// <summary>Checks exactly the BCL files whose donor copy is more complete than the game's.</summary>
    [RelayCommand]
    private void SelectRecommended()
    {
        foreach (var item in AllDiffItems) item.IsSelectedForTransplant = item.IsRecommended && item.CanTransplant;
        int selected = AllDiffItems.Count(x => x.IsSelectedForTransplant);
        var missingCritical = AllDiffItems
            .Where(x => x.Status == AssemblyDiffStatus.MissingInGame && x.IsCriticalBcl)
            .Select(x => x.FileName).ToArray();
        StatusSummary = selected == 0
            ? "권장할 BCL이 없습니다. 도너 쪽이 더 온전한 BCL 파일이 없습니다."
            : $"이식 대상 {selected}개를 자동 선택했습니다: {string.Join(", ", AllDiffItems.Where(x => x.IsSelectedForTransplant).Select(x => x.FileName))}. [3. 안전 이식] 탭에서 실행하세요." +
              (missingCritical.Length > 0 ? $"\n게임에 없는 핵심 BCL {missingCritical.Length}개는 선택하지 않았습니다(모드가 필요로 하면 직접 체크): {string.Join(", ", missingCritical)}" : string.Empty);
    }

    partial void OnGameManagedPathChanged(string value)
    {
        InvalidateScan();
        // Candidates were ranked against the previous target; they say nothing about this one.
        SelectedDonorCandidate = null;
        DonorCandidates = new ObservableCollection<DonorCandidate>();
        DonorSearchStatus = "대상이 바뀌었습니다. [도너 자동 찾기]를 다시 실행하세요.";
    }

    partial void OnDonorBclPathChanged(string value)
    {
        InvalidateScan();
        if (SelectedDonorCandidate != null && !string.Equals(SelectedDonorCandidate.BclPath, value, StringComparison.OrdinalIgnoreCase))
            SelectedDonorCandidate = null;
    }

    private void InvalidateScan()
    {
        AllDiffItems.Clear();
        FilteredDiffItems.Clear();
        SelectedItem = null;
        SymbolSearchResult = string.Empty;
        StatusSummary = "경로에 맞는 비교 스캔을 실행하세요.";
    }

    [RelayCommand]
    private void SearchSymbol()
    {
        if (SelectedItem == null)
        {
            SymbolSearchResult = "먼저 리스트에서 검사할 어셈블리 항목을 선택하세요.";
            return;
        }

        string targetPath = SelectedItem.GameExists
            ? Path.Combine(GameManagedPath, SelectedItem.FileName)
            : Path.Combine(DonorBclPath, SelectedItem.FileName);

        var (found, details) = _inspectorService.CheckMemberExistsInAssembly(targetPath, SymbolSearchQuery);
        SymbolSearchResult = found
            ? $"[성공] {details}"
            : $"[미발견/스트리핑] {details}";
    }

    /// <summary>Donor-only DLLs are mostly the donor game's own dependencies, so only BCL names are toggled.</summary>
    [RelayCommand]
    private void ToggleSelectAllMissing()
    {
        var missing = AllDiffItems.Where(x => x.Status == AssemblyDiffStatus.MissingInGame && x.IsBcl && x.CanTransplant).ToList();
        bool anyUnchecked = missing.Any(x => !x.IsSelectedForTransplant);
        foreach (var item in missing) item.IsSelectedForTransplant = anyUnchecked;
    }

    partial void OnFilterOnlyMissingChanged(bool value) => ApplyFilter();
    partial void OnFilterOnlyBclChanged(bool value) => ApplyFilter();

    private void ApplyFilter()
    {
        IEnumerable<AssemblyDiffItem> items = AllDiffItems;
        if (FilterOnlyBcl) items = items.Where(x => x.IsBcl);
        if (FilterOnlyMissing) items = items.Where(x => x.Status != AssemblyDiffStatus.Identical && x.Status != AssemblyDiffStatus.ExtraInGame);
        FilteredDiffItems = new ObservableCollection<AssemblyDiffItem>(items);
    }
}

