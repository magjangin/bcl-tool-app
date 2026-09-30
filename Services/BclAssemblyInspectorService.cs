using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using BclToolApp.Models;
using Mono.Cecil;

namespace BclToolApp.Services;

public class BclAssemblyInspectorService
{
    public List<AssemblyDiffItem> CompareDirectories(string gameManagedPath, string donorBclPath)
    {
        var result = new List<AssemblyDiffItem>();
        if (!Directory.Exists(gameManagedPath) || !Directory.Exists(donorBclPath))
            throw new DirectoryNotFoundException("게임 및 Donor 폴더가 모두 존재해야 합니다.");

        var gameDlls = Directory.Exists(gameManagedPath)
            ? Directory.GetFiles(gameManagedPath, "*.dll").Select(Path.GetFileName).Where(f => f != null).ToHashSet(StringComparer.OrdinalIgnoreCase)
            : new HashSet<string?>(StringComparer.OrdinalIgnoreCase);

        var donorDlls = Directory.Exists(donorBclPath)
            ? Directory.GetFiles(donorBclPath, "*.dll").Select(Path.GetFileName).Where(f => f != null).ToHashSet(StringComparer.OrdinalIgnoreCase)
            : new HashSet<string?>(StringComparer.OrdinalIgnoreCase);

        var allDllNames = gameDlls.Concat(donorDlls).Where(f => f != null).Select(f => f!).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(x => x);

        foreach (var dllName in allDllNames)
        {
            bool inGame = gameDlls.Contains(dllName);
            bool inDonor = donorDlls.Contains(dllName);

            var item = new AssemblyDiffItem
            {
                FileName = dllName,
                GameExists = inGame,
                DonorExists = inDonor,
                IsCriticalBcl = BclAssemblyCatalog.IsCritical(dllName),
                IsBcl = BclAssemblyCatalog.IsBcl(dllName),
                IsGameOrEngineCode = BclAssemblyCatalog.IsGameOrEngineCode(dllName),
                GameSize = inGame ? BclAssemblyCatalog.SizeOf(gameManagedPath, dllName) : 0,
                DonorSize = inDonor ? BclAssemblyCatalog.SizeOf(donorBclPath, dllName) : 0
            };

            string gameFilePath = Path.Combine(gameManagedPath, dllName);
            string donorFilePath = Path.Combine(donorBclPath, dllName);

            if (inGame && File.Exists(gameFilePath))
            {
                var (ver, tf) = ReadAssemblyInfo(gameFilePath);
                item.GameVersion = ver;
                item.GameTargetFramework = tf;
            }

            if (inDonor && File.Exists(donorFilePath))
            {
                var (ver, tf) = ReadAssemblyInfo(donorFilePath);
                item.DonorVersion = ver;
                item.DonorTargetFramework = tf;
            }

            if ((inGame && item.GameVersion == "Unknown") || (inDonor && item.DonorVersion == "Unknown"))
            {
                item.Status = AssemblyDiffStatus.InvalidAssembly;
                item.Notes = "관리형 어셈블리가 아니거나 파일을 읽을 수 없습니다.";
            }
            else if (!inGame && inDonor)
            {
                item.Status = AssemblyDiffStatus.MissingInGame;
                item.IsSelectedForTransplant = false;
                // Most donor-only DLLs are the donor game's own dependencies, not BCL the target is missing.
                item.Notes = item.IsCriticalBcl ? "⚠️ 핵심 BCL이 게임에 없습니다. 모드가 필요로 하면 추가하세요."
                    : item.IsBcl ? "게임에 없는 BCL (프로파일 차이일 수 있음)"
                    : $"도너 전용 파일 · {item.Kind} · 이식 대상 아님";
            }
            else if (inGame && !inDonor)
            {
                item.Status = AssemblyDiffStatus.ExtraInGame;
                item.Notes = "게임 고유 어셈블리 또는 커스텀 모드";
            }
            else
            {
                if (item.GameVersion != item.DonorVersion || item.GameTargetFramework != item.DonorTargetFramework)
                {
                    item.Status = AssemblyDiffStatus.VersionMismatch;
                    if (string.Equals(dllName, "mscorlib.dll", StringComparison.OrdinalIgnoreCase))
                    {
                        item.Notes = "⛔ 주의: mscorlib 버전 교체는 System.Object ABI 충돌 위험이 큽니다.";
                    }
                    else
                    {
                        item.Notes = "버전 또는 런타임 프로파일 차이";
                    }
                }
                else
                {
                    using var gameStream = File.OpenRead(gameFilePath);
                    using var donorStream = File.OpenRead(donorFilePath);
                    bool identical = SHA256.HashData(gameStream).SequenceEqual(SHA256.HashData(donorStream));
                    item.Status = identical ? AssemblyDiffStatus.Identical : AssemblyDiffStatus.ContentMismatch;
                    item.Notes = identical ? "SHA-256 일치" : "버전은 같지만 바이너리가 다릅니다. API 및 런타임 호환성을 확인하세요.";
                }
            }

            // A stripped BCL is smaller than the intact one; that is the signal the transplant reverses.
            item.IsRecommended = item.IsBcl && inGame && inDonor && item.DonorSize > item.GameSize &&
                item.Status is AssemblyDiffStatus.ContentMismatch or AssemblyDiffStatus.VersionMismatch;
            if (item.IsRecommended)
                item.Notes = $"✅ 이식 권장 · 게임 쪽이 {item.DonorSize - item.GameSize:N0}바이트 작습니다 · " + item.Notes;
            else if (item.IsGameOrEngineCode && item.Status is AssemblyDiffStatus.ContentMismatch or AssemblyDiffStatus.VersionMismatch)
                item.Notes = "⛔ 게임/엔진 코드 · 이식하면 다른 게임의 코드로 덮어씁니다";

            result.Add(item);
        }

        // What the user has to act on first: the BCL the transplant exists for, not the donor's own DLLs.
        return result
            .OrderByDescending(x => x.IsRecommended)
            .ThenByDescending(x => x.IsCriticalBcl)
            .ThenByDescending(x => x.IsBcl)
            .ThenBy(x => x.FileName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public (bool Found, string Details) CheckMemberExistsInAssembly(string assemblyPath, string searchTarget)
    {
        if (string.IsNullOrWhiteSpace(searchTarget))
            return (false, "검사할 타입 또는 메서드 이름을 입력하세요.");
        searchTarget = searchTarget.Trim().Replace('+', '/');
        if (!File.Exists(assemblyPath))
        {
            return (false, "어셈블리 파일이 존재하지 않습니다.");
        }

        try
        {
            var readerParams = new ReaderParameters { ReadSymbols = false };
            using var assembly = AssemblyDefinition.ReadAssembly(assemblyPath, readerParams);

            foreach (var module in assembly.Modules)
            {
                foreach (var type in module.GetTypes())
                {
                    if (type.FullName.Equals(searchTarget, StringComparison.OrdinalIgnoreCase))
                    {
                        return (true, $"타입 발견: {type.FullName} (메서드 수: {type.Methods.Count})");
                    }

                    foreach (var method in type.Methods)
                    {
                        string methodFull = $"{type.FullName}.{method.Name}";
                        if (methodFull.Equals(searchTarget, StringComparison.OrdinalIgnoreCase) ||
                            method.FullName.Equals(searchTarget, StringComparison.OrdinalIgnoreCase) ||
                            method.Name.Equals(searchTarget, StringComparison.OrdinalIgnoreCase))
                        {
                            return (true, $"메서드 발견: {method.FullName}");
                        }
                    }
                }
            }

            return (false, $"어셈블리 내에 '{searchTarget}' 심볼을 찾을 수 없습니다 (스트리핑되었거나 미포함).");
        }
        catch (Exception ex)
        {
            return (false, $"어셈블리 분석 중 오류: {ex.Message}");
        }
    }

    private static (string Version, string TargetFramework) ReadAssemblyInfo(string filePath)
    {
        try
        {
            using var asm = AssemblyDefinition.ReadAssembly(filePath);
            string version = asm.Name.Version?.ToString() ?? "0.0.0.0";
            string tf = asm.CustomAttributes
                .FirstOrDefault(a => a.AttributeType.Name == "TargetFrameworkAttribute")
                ?.ConstructorArguments.FirstOrDefault().Value?.ToString() ?? "Mono/NET";

            return (version, tf);
        }
        catch
        {
            return ("Unknown", "Unknown");
        }
    }
}
