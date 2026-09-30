using System;

namespace BclToolApp.Models;

/// <summary>Fingerprint of one Managed/BCL folder: what decides whether it can donate to another.</summary>
public sealed class BclProfile
{
    public string UnityVersion { get; init; } = string.Empty;
    /// <summary>Unity major.minor.patch, or null when the version could not be read.</summary>
    public (int Major, int Minor, int Patch)? Unity { get; init; }
    public int MscorlibMajor { get; init; }
    public int MscorlibMethods { get; init; }
    public long MscorlibSize { get; init; }
    public bool HasGetPEKind { get; init; }
    public bool Inspected { get; init; }
    public string UnityLine => Unity is { } u ? $"{u.Major}.{u.Minor}" : "미확인";
    public string Display => (UnityVersion.Length > 0 ? $"Unity {UnityVersion}" : "Unity 버전 미확인") +
        (MscorlibMajor > 0 ? $" · mscorlib {MscorlibMajor}.0" : " · mscorlib 미확인") +
        (Inspected ? $" · 메서드 {MscorlibMethods:N0}개 · {MscorlibSize:N0}바이트" : string.Empty);
}

/// <summary>One ranked donor folder. Ordering follows docs/02 §4: same Unity LTS line first, then
/// closest patch, then the most complete mscorlib.</summary>
public sealed class DonorCandidate
{
    public string Name { get; init; } = string.Empty;
    public string BclPath { get; init; } = string.Empty;
    public string Source { get; init; } = string.Empty;
    public BclProfile Profile { get; init; } = new();
    /// <summary>0 = same Unity LTS line, 1 = one side's version unknown, 2 = different line.</summary>
    public int LineRank { get; init; }
    /// <summary>0 = mscorlib as complete as the best candidate, 1 = measurably thinner (weakly stripped build).</summary>
    public int CompletenessRank { get; init; }
    public int PatchDistance { get; init; }
    public int SourceRank { get; init; }
    public string[] Warnings { get; init; } = Array.Empty<string>();

    public bool IsSafeLine => LineRank == 0 && CompletenessRank == 0;
    public string Title => $"{Name} · {Source}";
    public string Detail => Profile.Display;
    public string Reason => LineRank switch
    {
        0 => PatchDistance == 0 ? "같은 Unity 버전" : $"같은 LTS 줄 {Profile.UnityLine} · 패치 차이 {PatchDistance}",
        1 => "⚠️ Unity 버전을 확인하지 못했습니다. 엔진이 맞는지 직접 확인하세요.",
        _ => $"⚠️ 다른 LTS 줄({Profile.UnityLine}) · 엔진 불일치로 게임이 켜지지 않을 수 있습니다."
    } + (Warnings.Length > 0 ? " · " + string.Join(" · ", Warnings) : string.Empty);
    public string Color => LineRank == 0 && Warnings.Length == 0 ? "#A6E3A1" : LineRank == 2 ? "#F38BA8" : "#F9E2AF";
}
