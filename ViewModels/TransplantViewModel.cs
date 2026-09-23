using System;
using System.IO;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using BclToolApp.Services;

namespace BclToolApp.ViewModels;

public partial class TransplantViewModel : ObservableObject
{
    private readonly BclTransplantService _transplantService = new();
    private readonly AssemblyDiffViewModel _diffVm;
    private string _lastBackupGamePath = string.Empty;

    public TransplantViewModel(AssemblyDiffViewModel diffVm)
    {
        _diffVm = diffVm;
    }

    [ObservableProperty]
    private bool _autoBackup = true;

    [ObservableProperty]
    private string _executionLog = "이식할 어셈블리를 어셈블리 비교 탭에서 선택한 뒤 [안전 이식 실행]을 누르세요.";

    [ObservableProperty]
    private string _lastBackupPath = string.Empty;

    [ObservableProperty]
    private bool _hasBackup;

    [RelayCommand]
    private void ExecuteTransplant()
    {
        string donorPath = _diffVm.DonorBclPath;
        string gamePath = _diffVm.GameManagedPath;

        if (string.IsNullOrWhiteSpace(donorPath) || string.IsNullOrWhiteSpace(gamePath))
        {
            ExecutionLog = "[오류] 게임 Managed 경로와 Donor BCL 경로가 지정되지 않았습니다.";
            return;
        }

        var targets = _diffVm.AllDiffItems.Where(x => x.IsSelectedForTransplant && x.CanTransplant).Select(x => x.FileName).ToList();
        if (targets.Count == 0)
        {
            ExecutionLog = "[안내] 선택된 이식 대상 어셈블리가 없습니다. 비교 탭에서 체크박스를 선택하세요.";
            return;
        }

        ExecutionLog = $"[진행] {targets.Count}개 어셈블리 이식 작업을 시작합니다...\n";

        var res = _transplantService.ExecuteTransplant(donorPath, gamePath, targets, AutoBackup);

        if (!string.IsNullOrEmpty(res.BackupPath))
        {
            LastBackupPath = res.BackupPath;
            _lastBackupGamePath = Path.GetFullPath(gamePath);
            HasBackup = true;
            ExecutionLog += $"[백업 완료] 안전 백업 생성됨 -> {res.BackupPath}\n";
        }
        else if (res.Success)
        {
            LastBackupPath = string.Empty;
            _lastBackupGamePath = string.Empty;
            HasBackup = false;
            ExecutionLog += "[경고] 백업 없이 이식이 실행되어 이전 백업 복원 링크가 무효화되었습니다.\n";
        }

        foreach (var w in res.Warnings)
        {
            ExecutionLog += $"[경고] {w}\n";
        }

        foreach (var f in res.CopiedFiles)
        {
            ExecutionLog += $"[성공] 복사됨: {f}\n";
        }

        foreach (var e in res.Errors)
        {
            ExecutionLog += $"[실패] {e}\n";
        }

        ExecutionLog += $"\n[완료 요약] {res.LogSummary}";

        // 작업 후 Diff 갱신
        _diffVm.ScanCommand.Execute(null);
    }

    [RelayCommand]
    private void RestoreLastBackup()
    {
        if (string.IsNullOrEmpty(LastBackupPath) || !Directory.Exists(LastBackupPath))
        {
            ExecutionLog += "\n[오류] 복원할 백업 폴더를 찾을 수 없습니다.";
            return;
        }

        bool success = _transplantService.RestoreBackup(LastBackupPath, _lastBackupGamePath, out string err);
        if (success)
        {
            ExecutionLog += $"\n[복원 완료] {LastBackupPath} 에서 원래 파일로 성공적으로 복원되었습니다.";
            _diffVm.ScanCommand.Execute(null);
        }
        else
        {
            ExecutionLog += $"\n[복원 실패] {err}";
        }
    }
}

