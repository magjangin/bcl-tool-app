using System.Collections.Generic;

namespace BclToolApp.Models;

public enum BclStrippingStatus { NotScanned, Suspected, MissingFiles, NoProbeGaps, Inconclusive, NotSupported }

public sealed class BclStrippingResult
{
    public BclStrippingStatus Status { get; init; }
    public int CheckedApis { get; init; }
    public int MissingApis { get; init; }
    public IReadOnlyList<string> Evidence { get; init; } = new List<string>();
    public bool NeedsReview => Status is BclStrippingStatus.Suspected or BclStrippingStatus.MissingFiles;
    public int Priority => Status switch
    {
        BclStrippingStatus.Suspected => 0,
        BclStrippingStatus.MissingFiles => 1,
        BclStrippingStatus.Inconclusive => 2,
        BclStrippingStatus.NotScanned => 3,
        BclStrippingStatus.NoProbeGaps => 4,
        _ => 5
    };
    public string Summary => Status switch
    {
        BclStrippingStatus.Suspected => $"스트리핑 의심 · 표본 API {MissingApis}/{CheckedApis}개 누락",
        BclStrippingStatus.MissingFiles => "BCL 파일 누락 · 원인 확인 필요",
        BclStrippingStatus.NoProbeGaps => $"표본 API {CheckedApis}개 통과 · 전체 무결성은 미확인",
        BclStrippingStatus.Inconclusive => "판정 보류 · 읽기 오류 또는 지원하지 않는 프로파일",
        BclStrippingStatus.NotSupported => "IL2CPP/런타임 미확인 · BCL 검사 대상 아님",
        _ => "BCL 검사 전"
    };
    public string Color => Status switch
    {
        BclStrippingStatus.Suspected => "#F38BA8",
        BclStrippingStatus.MissingFiles => "#FAB387",
        BclStrippingStatus.NoProbeGaps => "#A6E3A1",
        _ => "#A6ADC8"
    };
    public string Details => Summary + "\n" + string.Join("\n", Evidence) +
        "\n현재 DLL의 표본 검사입니다. 누락은 스트리핑 또는 프로파일 차이일 수 있으며, 통과해도 다른 API의 스트리핑을 배제하지 않습니다. 이미 이식된 파일로 원래 상태를 복원 추정하지 않습니다.";
}
