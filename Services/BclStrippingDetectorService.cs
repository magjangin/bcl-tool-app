using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.IO;
using System.Linq;
using BclToolApp.Models;
using Mono.Cecil;

namespace BclToolApp.Services;

/// <summary>Read-only probes of metadata; never loads or executes game assemblies.</summary>
public sealed class BclStrippingDetectorService
{
    private sealed record CachedProbe(int Checked, int Missing, bool Inconclusive, string[] Evidence);
    private readonly ConcurrentDictionary<string, CachedProbe> _cache = new();

    public sealed record ApiProbe(string Assembly, string Type, string Method, int GenericArity, params string[] Parameters)
    {
        public string Label => $"{Type}.{Method}({string.Join(", ", Parameters)})";
    }

    // APIs present in classic Mono's .NET 2/3.5/4.x BCL. This is a sample, not a full reference API catalog.
    public static IReadOnlyList<ApiProbe> Probes { get; } = Array.AsReadOnly(new[]
    {
        new ApiProbe("mscorlib", "System.Activator", "CreateInstance", 0, "System.Type"),
        new ApiProbe("mscorlib", "System.Type", "GetMethod", 0, "System.String"),
        new ApiProbe("mscorlib", "System.Reflection.Assembly", "GetTypes", 0),
        new ApiProbe("mscorlib", "System.AppDomain", "GetAssemblies", 0),
        new ApiProbe("mscorlib", "System.Reflection.Emit.DynamicMethod", "GetILGenerator", 0),
        new ApiProbe("mscorlib", "System.Reflection.Emit.ILGenerator", "Emit", 0, "System.Reflection.Emit.OpCode"),
        new ApiProbe("mscorlib", "System.Reflection.Emit.ILGenerator", "Emit", 0, "System.Reflection.Emit.OpCode", "System.Reflection.MethodInfo"),
        new ApiProbe("mscorlib", "System.Collections.Generic.List`1", "Add", 0, "!0"),
        new ApiProbe("System.Core", "System.Linq.Enumerable", "Select", 2, "System.Collections.Generic.IEnumerable`1<!!0>", "System.Func`2<!!0,!!1>"),
        new ApiProbe("System.Core", "System.Linq.Enumerable", "Where", 1, "System.Collections.Generic.IEnumerable`1<!!0>", "System.Func`2<!!0,System.Boolean>"),
        new ApiProbe("System.Core", "System.Linq.Enumerable", "ToList", 1, "System.Collections.Generic.IEnumerable`1<!!0>"),
        new ApiProbe("System.Core", "System.Linq.Enumerable", "Any", 1, "System.Collections.Generic.IEnumerable`1<!!0>"),
        new ApiProbe("System.Core", "System.Linq.Expressions.Expression", "Constant", 0, "System.Object"),
        new ApiProbe("System.Core", "System.Linq.Expressions.Expression`1", "Compile", 0)
    });

    public BclStrippingResult Inspect(InstalledGame game)
    {
        if (!game.CanUseManaged)
            return new BclStrippingResult { Status = BclStrippingStatus.NotSupported };
        return InspectManagedDirectory(game.ManagedPath);
    }

    public BclStrippingResult InspectManagedDirectory(string managedPath)
    {
        var evidence = new List<string>();
        int checkedApis = 0, missingApis = 0;
        bool missingFile = false, inconclusive = false;
        if (!Directory.Exists(managedPath))
            return new BclStrippingResult { Status = BclStrippingStatus.Inconclusive, Evidence = new[] { "Managed 폴더가 존재하지 않습니다." } };
        foreach (var group in Probes.GroupBy(p => p.Assembly))
        {
            var path = Path.Combine(managedPath, group.Key + ".dll");
            if (!File.Exists(path))
            {
                missingFile = true;
                evidence.Add($"[파일 없음] {group.Key}.dll — 미사용 어셈블리 생략 또는 프로파일 차이도 가능합니다.");
                continue;
            }
            try
            {
                // Sequential read avoids thousands of small disk seeks; hash caches identical BCLs across games.
                var bytes = File.ReadAllBytes(path);
                var key = group.Key + ":" + Convert.ToHexString(SHA256.HashData(bytes));
                if (_cache.TryGetValue(key, out var cached))
                {
                    checkedApis += cached.Checked;
                    missingApis += cached.Missing;
                    inconclusive |= cached.Inconclusive;
                    evidence.AddRange(cached.Evidence);
                    continue;
                }
                int firstEvidence = evidence.Count, firstChecked = checkedApis, firstMissing = missingApis;
                bool forwardedProbe = false;
                using var stream = new MemoryStream(bytes, false);
                using var assembly = AssemblyDefinition.ReadAssembly(stream, new ReaderParameters { ReadSymbols = false, ReadingMode = ReadingMode.Deferred });
                if (assembly.Name.Name != group.Key ||
                    (group.Key == "mscorlib" && assembly.Name.Version.Major is not (2 or 4)) ||
                    (group.Key == "System.Core" && assembly.Name.Version.Major is not (3 or 4)))
                {
                    inconclusive = true;
                    evidence.Add($"[프로파일 미지원] {group.Key}.dll: {assembly.Name.FullName}");
                    continue;
                }
                var types = assembly.Modules.SelectMany(m => m.GetTypes()).ToDictionary(t => t.FullName);
                var forwarded = assembly.Modules.SelectMany(m => m.ExportedTypes).Select(t => t.FullName).ToHashSet();
                evidence.Add($"[메타데이터] {group.Key}.dll {assembly.Name.Version} · 타입 {types.Count}개");
                foreach (var probe in group)
                {
                    if (forwarded.Contains(probe.Type))
                    {
                        inconclusive = true;
                        forwardedProbe = true;
                        evidence.Add($"[타입 전달] {probe.Type} — 다른 어셈블리로 전달되어 이 검사에서 판정하지 않습니다.");
                        continue;
                    }
                    checkedApis++;
                    if (!types.TryGetValue(probe.Type, out var type))
                    {
                        missingApis++;
                        evidence.Add($"[타입 누락] {group.Key}.dll → {probe.Type}");
                    }
                    else if (!type.Methods.Any(m => m.IsPublic && m.Name == probe.Method &&
                        m.GenericParameters.Count == probe.GenericArity &&
                        m.Parameters.Select(p => Signature(p.ParameterType)).SequenceEqual(probe.Parameters)))
                    {
                        missingApis++;
                        evidence.Add($"[메서드 누락] {group.Key}.dll → {probe.Label}");
                    }
                }
                _cache[key] = new CachedProbe(checkedApis - firstChecked, missingApis - firstMissing, forwardedProbe, evidence.Skip(firstEvidence).ToArray());
            }
            catch (Exception ex)
            {
                inconclusive = true;
                evidence.Add($"[읽기 오류] {group.Key}.dll: {ex.Message}");
            }
        }
        return new BclStrippingResult
        {
            Status = missingApis > 0 ? BclStrippingStatus.Suspected : missingFile ? BclStrippingStatus.MissingFiles :
                inconclusive || checkedApis == 0 ? BclStrippingStatus.Inconclusive : BclStrippingStatus.NoProbeGaps,
            CheckedApis = checkedApis, MissingApis = missingApis, Evidence = evidence.Distinct().ToArray()
        };
    }

    private static string Signature(TypeReference type) => type switch
    {
        GenericParameter p => (p.Type == GenericParameterType.Method ? "!!" : "!") + p.Position,
        GenericInstanceType g => g.ElementType.FullName + "<" + string.Join(",", g.GenericArguments.Select(Signature)) + ">",
        _ => type.FullName
    };
}


