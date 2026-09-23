using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using BclToolApp.Models;
using BclToolApp.Services;

namespace BclToolApp.ViewModels;

public partial class AssemblyDiffViewModel : ObservableObject
{
    private readonly BclAssemblyInspectorService _inspectorService = new();

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

    [ObservableProperty]
    private string _statusSummary = "비교할 게임 및 Donor BCL 디렉토리를 지정하세요.";

    [ObservableProperty]
    private string _symbolSearchQuery = "System.Linq.Enumerable.Select";

    [ObservableProperty]
    private string _symbolSearchResult = string.Empty;

    [ObservableProperty]
    private AssemblyDiffItem? _selectedItem;

    [RelayCommand]
    private void Scan()
    {
        InvalidateScan();
        if (!Directory.Exists(GameManagedPath) || !Directory.Exists(DonorBclPath))
        {
            StatusSummary = "게임 및 Donor 폴더가 모두 존재해야 합니다.";
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

    partial void OnGameManagedPathChanged(string value) => InvalidateScan();
    partial void OnDonorBclPathChanged(string value) => InvalidateScan();

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

    [RelayCommand]
    private void ToggleSelectAllMissing()
    {
        bool anyUnchecked = AllDiffItems.Any(x => x.Status == AssemblyDiffStatus.MissingInGame && !x.IsSelectedForTransplant);
        foreach (var item in AllDiffItems.Where(x => x.Status == AssemblyDiffStatus.MissingInGame))
        {
            item.IsSelectedForTransplant = anyUnchecked;
        }
    }

    partial void OnFilterOnlyMissingChanged(bool value)
    {
        ApplyFilter();
    }

    private void ApplyFilter()
    {
        if (FilterOnlyMissing)
        {
            FilteredDiffItems = new ObservableCollection<AssemblyDiffItem>(
                AllDiffItems.Where(x => x.Status != AssemblyDiffStatus.Identical && x.Status != AssemblyDiffStatus.ExtraInGame));
        }
        else
        {
            FilteredDiffItems = new ObservableCollection<AssemblyDiffItem>(AllDiffItems);
        }
    }
}

