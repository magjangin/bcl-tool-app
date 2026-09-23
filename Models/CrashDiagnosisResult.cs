namespace BclToolApp.Models;

public enum CrashCategory
{
    BclStrippedApi,          // BCL API/메서드 스트리핑 (예: Enumerable.Select, Reflection.Emit)
    BclMissingAssembly,       // BCL 어셈블리 누락 (예: System.Core.dll 없음)
    GameCodeStripping,       // 게임 코드 스트리핑 (Assembly-CSharp 등 - BCL 이식 대상 아님!)
    InteropAbiMismatch,      // BCL ABI / System.Object 정체성 충돌 (잘못된 BCL 주입 등)
    NativeOrJniIssue,        // Native .so/.dll 또는 JNI/il2cpp 내부 크래시
    LoaderBootstrap,         // 모드로더 자체 부트스트랩 실패
    NormalOrUnknown          // 관리형 예외 미검출 또는 알 수 없음
}

public class CrashDiagnosisResult
{
    public CrashCategory Category { get; set; } = CrashCategory.NormalOrUnknown;
    public string CategoryDisplayName => Category switch
    {
        CrashCategory.BclStrippedApi => "⚠️ BCL API 스트리핑 (MissingMethod / TypeLoad)",
        CrashCategory.BclMissingAssembly => "❌ BCL 어셈블리 파일 누락 (FileNotFound)",
        CrashCategory.GameCodeStripping => "⛔ 게임 코드 스트리핑 (BCL 이식 대상 아님)",
        CrashCategory.InteropAbiMismatch => "💥 ABI / BCL 충돌 (System.Object/Runtime 불일치)",
        CrashCategory.NativeOrJniIssue => "🛑 Native / JNI / IL2CPP 엔진 크래시",
        CrashCategory.LoaderBootstrap => "🔧 모드로더 부트스트랩 오류",
        _ => "❓ 알 수 없거나 정상 로그"
    };

    public string BadgeColor => Category switch
    {
        CrashCategory.BclStrippedApi => "#E67E22",      // Orange
        CrashCategory.BclMissingAssembly => "#E74C3C",  // Red
        CrashCategory.GameCodeStripping => "#9B59B6",   // Purple (Notice: Not BCL!)
        CrashCategory.InteropAbiMismatch => "#C0392B",  // Dark Red
        CrashCategory.NativeOrJniIssue => "#7F8C8D",    // Gray
        CrashCategory.LoaderBootstrap => "#2980B9",     // Blue
        _ => "#27AE60"                                  // Green
    };

    public string ExceptionType { get; set; } = string.Empty;
    public string TargetAssembly { get; set; } = string.Empty;
    public string TargetMember { get; set; } = string.Empty;
    public string Summary { get; set; } = string.Empty;
    public string DetailedExplanation { get; set; } = string.Empty;
    public string Recommendation { get; set; } = string.Empty;
    public bool IsBclTransplantCandidate => Category is CrashCategory.BclStrippedApi or CrashCategory.BclMissingAssembly;
    public string ExtractedContext { get; set; } = string.Empty;
}
