using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using BclToolApp.Models;
using Mono.Cecil;

namespace BclToolApp.Services;

/// <summary>Ranks donor BCL folders for one stripped game. Read-only: reads metadata, never writes.
/// The criteria come from docs/02 §4 — same Unity LTS line, a donor mscorlib that still has
/// Module.GetPEKind and is as complete as its peers, then the closest patch. Engine file size is
/// deliberately not used: two builds with the same mono-2.0-bdwgc.dll size differ by megabytes.</summary>
public sealed class DonorSearchService
{
    // mscorlib is megabytes, so it is opened only for the best-ranked candidates and only once each.
    // The whole window is inspected even after enough candidates pass, because the method counts of the
    // peers are what tell a complete mscorlib from a thinner profile.
    private const int DeepInspectLimit = 30;
    private const int ResultLimit = 10;

    private static readonly StringComparison PathComparison = OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    // Unity stamps "2019.4.27f1" into globalgamemanagers; UnityPlayer.dll may only carry "2019.4.27.43569".
    private static readonly Regex StampedVersion = new(@"\b(\d{1,4})\.(\d{1,2})\.(\d{1,3})[fpbax]\d{1,3}\b", RegexOptions.Compiled);
    private static readonly Regex PlainVersion = new(@"\b(\d{1,4})\.(\d{1,2})\.(\d{1,3})\b", RegexOptions.Compiled);

    private sealed record DeepInfo(int Major, int Methods, bool HasGetPEKind, bool IsReferenceAssembly, string? Error);
    private readonly ConcurrentDictionary<string, DeepInfo> _deepCache = new();

    public sealed record SearchResult(BclProfile Target, IReadOnlyList<DonorCandidate> Candidates, IReadOnlyList<string> Notes)
    {
        public DonorCandidate? Best => Candidates.FirstOrDefault();
        /// <summary>Only a same-LTS-line donor is filled in automatically; anything else needs a human decision.</summary>
        public DonorCandidate? AutoApply => Best is { IsSafeLine: true } best ? best : null;
    }

    private sealed record Raw(string Name, string BclPath, string Source, int SourceRank, string UnityVersion, (int Major, int Minor, int Patch)? Unity);

    public SearchResult Find(string targetManagedPath, IEnumerable<InstalledGame> games,
        bool includeUnityEditors = true, CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(targetManagedPath))
            throw new DirectoryNotFoundException("대상 게임의 Managed 폴더가 존재하지 않습니다.");
        var target = Normalize(targetManagedPath);
        var targetRoot = Path.GetDirectoryName(Path.GetDirectoryName(target));
        var targetUnity = targetRoot != null ? ReadUnityVersion(targetRoot) : string.Empty;
        var targetProfile = InspectBcl(target, targetUnity);
        var notes = new List<string>();
        if (targetProfile.MscorlibMajor == 0)
            notes.Add("대상 mscorlib.dll을 읽지 못했습니다. 프로파일 대신 Unity 버전만으로 거릅니다.");
        if (targetProfile.Unity == null)
            notes.Add("대상 Unity 버전을 확인하지 못했습니다(UnityPlayer.dll / globalgamemanagers). 후보의 엔진 일치는 직접 확인하세요.");

        var rejected = new Dictionary<string, int>();
        void Reject(string reason) => rejected[reason] = rejected.GetValueOrDefault(reason) + 1;

        var raws = new List<Raw>();
        foreach (var game in games ?? Array.Empty<InstalledGame>())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!game.CanUseManaged || !Directory.Exists(game.ManagedPath)) continue;
            if (string.Equals(Normalize(game.ManagedPath), target, PathComparison)) continue;
            var version = ReadUnityVersion(game.InstallPath);
            raws.Add(new Raw(game.Name, Normalize(game.ManagedPath), "설치 게임", 0, version, Parse(version)));
        }
        if (includeUnityEditors)
            foreach (var editor in FindUnityEditorProfiles(cancellationToken))
                raws.Add(editor);

        // Ranking first, file reads second: the Unity version and the file size cost no mscorlib read.
        var scored = new List<(Raw Raw, long Size, int LineRank, int PatchDistance)>();
        foreach (var raw in raws)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var mscorlib = Path.Combine(raw.BclPath, "mscorlib.dll");
            if (!File.Exists(mscorlib)) { Reject("mscorlib.dll 없음"); continue; }
            long size;
            try { size = new FileInfo(mscorlib).Length; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Reject("파일 접근 실패"); continue; }
            var (lineRank, distance) = CompareLines(targetProfile.Unity, raw.Unity);
            scored.Add((raw, size, lineRank, distance));
        }

        var accepted = new List<(Raw Raw, long Size, int LineRank, int PatchDistance, int Methods, int Major)>();
        int inspected = 0;
        foreach (var entry in scored
            .OrderBy(x => x.LineRank).ThenBy(x => x.PatchDistance).ThenBy(x => x.Raw.SourceRank).ThenByDescending(x => x.Size))
        {
            if (inspected >= DeepInspectLimit) break;
            cancellationToken.ThrowIfCancellationRequested();
            inspected++;
            var deep = DeepInspect(Path.Combine(entry.Raw.BclPath, "mscorlib.dll"));
            if (deep.Error != null) { Reject("mscorlib 정밀 분석 실패"); continue; }
            if (deep.Major == 0) { Reject("mscorlib 어셈블리가 아님"); continue; }
            if (targetProfile.MscorlibMajor > 0 && deep.Major != targetProfile.MscorlibMajor)
            { Reject($"mscorlib {deep.Major}.x — 대상({targetProfile.MscorlibMajor}.x)과 다른 프로파일"); continue; }
            if (deep.IsReferenceAssembly) { Reject("참조 전용 어셈블리(IL 없음)"); continue; }
            if (!deep.HasGetPEKind) { Reject("Module.GetPEKind 없음 — 도너도 스트리핑됨"); continue; }
            accepted.Add((entry.Raw, entry.Size, entry.LineRank, entry.PatchDistance, deep.Methods, deep.Major));
        }

        // Measured in docs/02 §4-2: Mask of Mists matches Neon Abyss's engine exactly but its mscorlib has 926
        // fewer methods, so a slightly farther patch with a complete mscorlib is the better donor.
        var fullest = accepted.GroupBy(a => a.LineRank).ToDictionary(g => g.Key, g => g.Max(a => a.Methods));
        var candidates = new List<DonorCandidate>();
        foreach (var entry in accepted)
        {
            int best = fullest[entry.LineRank];
            bool thin = entry.Methods < best * 0.99;
            var warnings = new List<string>();
            if (thin) warnings.Add($"같은 줄 후보 중 mscorlib이 가장 온전하지 않습니다(최대 {best:N0}개)");
            if (targetProfile.Inspected && entry.Methods <= targetProfile.MscorlibMethods)
                warnings.Add($"mscorlib 메서드가 대상({targetProfile.MscorlibMethods:N0}개)보다 많지 않습니다");
            candidates.Add(new DonorCandidate
            {
                Name = entry.Raw.Name,
                BclPath = entry.Raw.BclPath,
                Source = entry.Raw.Source,
                SourceRank = entry.Raw.SourceRank,
                LineRank = entry.LineRank,
                CompletenessRank = thin ? 1 : 0,
                PatchDistance = entry.PatchDistance,
                Warnings = warnings.ToArray(),
                Profile = new BclProfile
                {
                    UnityVersion = entry.Raw.UnityVersion,
                    Unity = entry.Raw.Unity,
                    MscorlibMajor = entry.Major,
                    MscorlibMethods = entry.Methods,
                    MscorlibSize = entry.Size,
                    HasGetPEKind = true,
                    Inspected = true
                }
            });
        }

        var ranked = candidates
            .OrderBy(c => c.LineRank).ThenBy(c => c.CompletenessRank).ThenBy(c => c.PatchDistance).ThenBy(c => c.SourceRank)
            .ThenByDescending(c => c.Profile.MscorlibMethods).ThenByDescending(c => c.Profile.MscorlibSize)
            .ToList();
        // A same-line donor is the only kind with measured successes; listing worse tiers beside it invites a bad pick.
        if (ranked.Any(c => c.LineRank == 0)) ranked = ranked.Where(c => c.LineRank == 0).ToList();
        else if (ranked.Count > 0) notes.Add("같은 Unity LTS 줄의 도너가 없습니다. 아래 후보는 엔진이 다를 수 있어 자동 적용하지 않습니다.");

        if (rejected.Count > 0)
            notes.Add("제외된 후보: " + string.Join(", ", rejected.OrderByDescending(r => r.Value).Take(5).Select(r => $"{r.Key} {r.Value}개")));
        return new SearchResult(targetProfile, ranked.Take(ResultLimit).ToList(), notes);
    }

    /// <summary>Reads mscorlib metadata of one Managed/BCL folder. Missing or unreadable parts stay zero.</summary>
    public BclProfile InspectBcl(string bclPath, string unityVersion)
    {
        var mscorlib = Path.Combine(bclPath, "mscorlib.dll");
        if (!File.Exists(mscorlib))
            return new BclProfile { UnityVersion = unityVersion, Unity = Parse(unityVersion) };
        long size = 0;
        try { size = new FileInfo(mscorlib).Length; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        var deep = DeepInspect(mscorlib);
        return new BclProfile
        {
            UnityVersion = unityVersion,
            Unity = Parse(unityVersion),
            MscorlibMajor = deep.Major,
            MscorlibMethods = deep.Methods,
            MscorlibSize = size,
            HasGetPEKind = deep.HasGetPEKind,
            Inspected = deep.Error == null
        };
    }

    /// <summary>Unity version of an installed game: UnityPlayer.dll's version resource first, then the
    /// version Unity stamps into the head of *_Data/globalgamemanagers.</summary>
    public static string ReadUnityVersion(string gameRoot)
    {
        if (string.IsNullOrWhiteSpace(gameRoot) || !Directory.Exists(gameRoot)) return string.Empty;
        try
        {
            var player = Path.Combine(gameRoot, "UnityPlayer.dll");
            if (File.Exists(player))
            {
                var info = FileVersionInfo.GetVersionInfo(player);
                foreach (var text in new[] { info.ProductVersion, info.FileVersion })
                    if (Match(text) is { Length: > 0 } found) return found;
            }
            foreach (var data in Directory.GetDirectories(gameRoot, "*_Data").OrderBy(d => d))
                foreach (var name in new[] { "globalgamemanagers", "data.unity3d", "level0" })
                {
                    var path = Path.Combine(data, name);
                    if (!File.Exists(path)) continue;
                    if (ReadHeadText(path) is { } head && StampedVersion.Match(head) is { Success: true } match)
                        return match.Value;
                }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return string.Empty;
    }

    /// <summary>Unity editor Mono profiles (Unity Hub and standalone installs) as extra donor folders.</summary>
    private IEnumerable<Raw> FindUnityEditorProfiles(CancellationToken cancellationToken)
    {
        var roots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var folder in new[] { Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.ProgramFilesX86 })
        {
            var path = Environment.GetFolderPath(folder);
            if (path.Length > 0) roots.Add(Path.Combine(path, "Unity"));
        }
        try
        {
            foreach (var drive in DriveInfo.GetDrives().Where(d => d.IsReady && d.DriveType == DriveType.Fixed))
                roots.Add(Path.Combine(drive.RootDirectory.FullName, "Unity"));
        }
        catch (IOException) { }

        var editors = new List<string>();
        foreach (var root in roots.Where(Directory.Exists))
        {
            editors.Add(root);
            foreach (var hub in new[] { Path.Combine(root, "Hub", "Editor"), Path.Combine(root, "Editor") })
                if (Directory.Exists(hub))
                    try { editors.AddRange(Directory.GetDirectories(hub)); }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }

        foreach (var editor in editors.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var version = Match(Path.GetFileName(Path.TrimEndingDirectorySeparator(editor)));
            if (version.Length == 0)
            {
                var exe = Path.Combine(editor, "Editor", "Unity.exe");
                if (File.Exists(exe))
                    try { version = Match(FileVersionInfo.GetVersionInfo(exe).ProductVersion); }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
            foreach (var runtime in new[] { "MonoBleedingEdge", "Mono" })
            {
                var lib = Path.Combine(editor, "Editor", "Data", runtime, "lib", "mono");
                if (!Directory.Exists(lib)) continue;
                string[] profiles;
                try { profiles = Directory.GetDirectories(lib); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }
                foreach (var profile in profiles)
                {
                    var name = Path.GetFileName(profile);
                    // *-api folders hold reference assemblies with no IL; they must never reach a game folder.
                    if (name.EndsWith("-api", StringComparison.OrdinalIgnoreCase)) continue;
                    if (!File.Exists(Path.Combine(profile, "mscorlib.dll"))) continue;
                    yield return new Raw($"Unity {(version.Length > 0 ? version : "버전 미확인")} ({runtime}/{name})",
                        Normalize(profile), "Unity 에디터", 1, version, Parse(version));
                }
            }
        }
    }

    private DeepInfo DeepInspect(string mscorlibPath)
    {
        string key;
        try
        {
            var file = new FileInfo(mscorlibPath);
            key = $"{Normalize(mscorlibPath)}|{file.Length}|{file.LastWriteTimeUtc.Ticks}";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return new DeepInfo(0, 0, false, false, ex.Message); }
        if (_deepCache.TryGetValue(key, out var cached)) return cached;
        DeepInfo info;
        try
        {
            using var assembly = AssemblyDefinition.ReadAssembly(mscorlibPath,
                new ReaderParameters { ReadSymbols = false, ReadingMode = ReadingMode.Deferred, InMemory = true });
            int methods = 0;
            bool peKind = false;
            foreach (var type in assembly.Modules.SelectMany(m => m.GetTypes()))
            {
                methods += type.Methods.Count;
                if (!peKind && type.FullName == "System.Reflection.Module")
                    peKind = type.Methods.Any(m => m.IsPublic && m.Name == "GetPEKind");
            }
            info = new DeepInfo(assembly.Name.Name == "mscorlib" ? assembly.Name.Version.Major : 0, methods, peKind,
                assembly.CustomAttributes.Any(a => a.AttributeType.FullName == "System.Runtime.CompilerServices.ReferenceAssemblyAttribute"),
                null);
        }
        catch (Exception ex) { info = new DeepInfo(0, 0, false, false, ex.Message); }
        _deepCache[key] = info;
        return info;
    }

    private static (int LineRank, int PatchDistance) CompareLines((int Major, int Minor, int Patch)? target, (int Major, int Minor, int Patch)? candidate)
    {
        if (target is not { } t || candidate is not { } c) return (1, int.MaxValue);
        if (t.Major != c.Major || t.Minor != c.Minor) return (2, int.MaxValue);
        return (0, Math.Abs(t.Patch - c.Patch));
    }

    public static (int Major, int Minor, int Patch)? Parse(string? version)
    {
        if (string.IsNullOrWhiteSpace(version)) return null;
        var match = StampedVersion.Match(version);
        if (!match.Success) match = PlainVersion.Match(version);
        if (!match.Success) return null;
        return (int.Parse(match.Groups[1].Value), int.Parse(match.Groups[2].Value), int.Parse(match.Groups[3].Value));
    }

    private static string Match(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;
        var stamped = StampedVersion.Match(text);
        if (stamped.Success) return stamped.Value;
        var plain = PlainVersion.Match(text);
        return plain.Success ? plain.Value : string.Empty;
    }

    private static string? ReadHeadText(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var buffer = new byte[4096];
            int read = stream.Read(buffer, 0, buffer.Length);
            var text = new StringBuilder(read);
            for (int i = 0; i < read; i++) text.Append(buffer[i] is >= 0x20 and < 0x7F ? (char)buffer[i] : '\n');
            return text.ToString();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    private static string Normalize(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
}
