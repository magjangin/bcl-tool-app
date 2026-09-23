using CommunityToolkit.Mvvm.ComponentModel;

namespace BclToolApp.Models;

public enum AssemblyDiffStatus
{
    Identical,          // 동일함
    MissingInGame,      // 게임 Managed 폴더에 누락됨 (이식 우선 후보!)
    VersionMismatch,    // 버전 또는 타겟 프레임워크 불일치
    ExtraInGame,         // 게임에만 존재 (게임 자체 코드 또는 커스텀 dll)
    ContentMismatch,
    InvalidAssembly
}

public partial class AssemblyDiffItem : ObservableObject
{
    public string FileName { get; set; } = string.Empty;
    public bool GameExists { get; set; }
    public bool DonorExists { get; set; }
    public string GameVersion { get; set; } = "-";
    public string DonorVersion { get; set; } = "-";
    public string GameTargetFramework { get; set; } = "-";
    public string DonorTargetFramework { get; set; } = "-";
    public AssemblyDiffStatus Status { get; set; }

    public string StatusDisplayName => Status switch
    {
        AssemblyDiffStatus.MissingInGame => "❌ 게임 누락 (검토 필요)",
        AssemblyDiffStatus.VersionMismatch => "⚠️ 버전/프로파일 차이",
        AssemblyDiffStatus.Identical => "✅ 일치함",
        AssemblyDiffStatus.ExtraInGame => "📦 게임 고유 DLL",
        AssemblyDiffStatus.ContentMismatch => "⚠️ 바이너리 내용 차이",
        AssemblyDiffStatus.InvalidAssembly => "⛔ 분석 불가",
        _ => "-"
    };

    public string StatusColor => Status switch
    {
        AssemblyDiffStatus.MissingInGame => "#E74C3C",    // Red
        AssemblyDiffStatus.VersionMismatch => "#F39C12", // Orange
        AssemblyDiffStatus.Identical => "#2ECC71",       // Green
        AssemblyDiffStatus.ContentMismatch => "#F39C12",
        AssemblyDiffStatus.InvalidAssembly => "#C0392B",
        AssemblyDiffStatus.ExtraInGame => "#3498DB",      // Blue
        _ => "#BDC3C7"
    };

    public bool IsCriticalBcl { get; set; }
    public bool CanTransplant => DonorExists && Status != AssemblyDiffStatus.InvalidAssembly;
    [ObservableProperty]
    private bool _isSelectedForTransplant;
    public string Notes { get; set; } = string.Empty;
}

