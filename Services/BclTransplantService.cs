using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using Mono.Cecil;

namespace BclToolApp.Services;

public class BclTransplantService
{
    private const string ManifestName = ".bcl-backup.json";
    private static readonly StringComparison PathComparison = OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    public class TransplantResult
    {
        public bool Success { get; set; }
        public string BackupPath { get; set; } = string.Empty;
        public List<string> CopiedFiles { get; } = new();
        public List<string> Errors { get; } = new();
        public List<string> Warnings { get; } = new();
        public string LogSummary { get; set; } = string.Empty;
    }

    public class BackupManifest
    {
        public string TargetPath { get; set; } = string.Empty;
        public Dictionary<string, string> Files { get; set; } = new();
        public List<string> AddedFiles { get; set; } = new();
        public List<string> ReplacedFiles { get; set; } = new();
    }

    private static string Normalize(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
    private static string Hash(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    private static void RejectLink(string path)
    {
        // Check ancestors too: a normal file inside a junction still redirects writes.
        for (string? current = Path.GetFullPath(path); current != null; current = Path.GetDirectoryName(current))
            if ((File.Exists(current) || Directory.Exists(current)) &&
                (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new IOException($"링크 경로는 지원하지 않습니다: {current}");
    }

    private static bool IsFileName(string name) => !string.IsNullOrWhiteSpace(name) &&
        name != "." && name != ".." && !Path.IsPathRooted(name) &&
        name.IndexOfAny(new[] { '/', '\\', ':' }) < 0 &&
        name.IndexOfAny(Path.GetInvalidFileNameChars()) < 0;

    public string CreateBackup(string gameManagedPath) => CreateBackup(gameManagedPath, Array.Empty<string>(), Array.Empty<string>());

    private string CreateBackup(string gameManagedPath, IEnumerable<string> addedFiles, IEnumerable<string> replacedFiles)
    {
        var target = Normalize(gameManagedPath);
        if (!Directory.Exists(target)) throw new DirectoryNotFoundException(target);
        RejectLink(target);
        var parent = Directory.GetParent(target)?.FullName ?? throw new IOException("루트 폴더는 백업할 수 없습니다.");
        var backup = Path.Combine(parent, $"{Path.GetFileName(target)}_Backup_{DateTime.Now:yyyyMMdd_HHmmss}_{Guid.NewGuid():N}");
        var manifest = new BackupManifest
        {
            TargetPath = target,
            AddedFiles = addedFiles.ToList(),
            ReplacedFiles = replacedFiles.ToList()
        };
        Directory.CreateDirectory(backup);
        // The transplant only changes top-level DLLs. Preserve all top-level original files.
        foreach (var file in Directory.GetFiles(target))
        {
            RejectLink(file);
            var name = Path.GetFileName(file);
            if (name == ManifestName) throw new IOException("백업 메타데이터 이름과 충돌합니다.");
            File.Copy(file, Path.Combine(backup, name));
            manifest.Files.Add(name, Hash(Path.Combine(backup, name)));
        }
        File.WriteAllText(Path.Combine(backup, ManifestName), JsonSerializer.Serialize(manifest));
        return backup;
    }

    public TransplantResult ExecuteTransplant(string donorBclPath, string gameManagedPath,
        IEnumerable<string> fileNames, bool createBackupFirst)
    {
        var result = new TransplantResult();
        // Keep a private journal even when the user disables the persistent backup.
        var originals = new Dictionary<string, byte[]?>();
        var attempted = new List<string>();
        try
        {
            var donor = Normalize(donorBclPath);
            var target = Normalize(gameManagedPath);
            if (!Directory.Exists(donor) || !Directory.Exists(target))
                throw new DirectoryNotFoundException("게임 및 Donor 폴더가 모두 존재해야 합니다.");
            if (string.Equals(donor, target, PathComparison))
                throw new IOException("게임과 Donor 폴더는 달라야 합니다.");
            RejectLink(donor);
            RejectLink(target);
            var names = (fileNames ?? throw new ArgumentNullException(nameof(fileNames)))
                .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (names.Count == 0) throw new ArgumentException("이식 대상이 없습니다.");
            var sources = new Dictionary<string, byte[]>();
            foreach (var name in names)
            {
                if (!IsFileName(name) || !name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
                    throw new ArgumentException($"잘못된 DLL 파일명: {name}");
                var source = Path.Combine(donor, name);
                var destination = Path.Combine(target, name);
                RejectLink(source);
                RejectLink(destination);
                var bytes = File.ReadAllBytes(source);
                using (var stream = new MemoryStream(bytes))
                using (var asm = AssemblyDefinition.ReadAssembly(stream))
                {
                    if (asm.CustomAttributes.Any(a => a.AttributeType.FullName == "System.Runtime.CompilerServices.ReferenceAssemblyAttribute"))
                        throw new InvalidOperationException($"참조 전용 어셈블리(Reference Assembly)는 이식할 수 없습니다: {name}");
                }
                sources.Add(name, bytes);
                originals.Add(destination, File.Exists(destination) ? File.ReadAllBytes(destination) : null);
                if (string.Equals(name, "mscorlib.dll", StringComparison.OrdinalIgnoreCase))
                    result.Warnings.Add("mscorlib.dll 교체는 런타임 ABI 충돌을 일으킬 수 있습니다. 호환성은 보장되지 않습니다.");
            }
            if (createBackupFirst)
            {
                var added = names.Where(n => originals[Path.Combine(target, n)] == null);
                var replaced = names.Where(n => originals[Path.Combine(target, n)] != null);
                result.BackupPath = CreateBackup(target, added, replaced);
            }
            foreach (var name in names)
            {
                var destination = Path.Combine(target, name);
                attempted.Add(destination);
                File.WriteAllBytes(destination, sources[name]);
                if (!File.ReadAllBytes(destination).SequenceEqual(sources[name]))
                    throw new IOException($"복사 검증 실패: {name}");
                result.CopiedFiles.Add(name);
            }
            result.Success = true;
        }
        catch (Exception ex)
        {
            result.Errors.Add($"이식 실패: {ex.Message}");
            foreach (var destination in attempted.AsEnumerable().Reverse())
            {
                try
                {
                    var original = originals[destination];
                    if (original == null) File.Delete(destination);
                    else File.WriteAllBytes(destination, original);
                    result.CopiedFiles.Remove(Path.GetFileName(destination));
                }
                catch (Exception rollbackError)
                {
                    result.Errors.Add($"자동 롤백 실패 ({destination}): {rollbackError.Message}");
                }
            }
        }
        result.LogSummary = $"{(result.Success ? "이식 완료" : "이식 실패")}: 복사 {result.CopiedFiles.Count}개, 오류 {result.Errors.Count}개, 경고 {result.Warnings.Count}개";
        return result;
    }

    public bool RestoreBackup(string backupPath, string gameManagedPath, out string error)
    {
        error = string.Empty;
        var originals = new Dictionary<string, byte[]?>();
        var attempted = new List<string>();
        try
        {
            var backup = Normalize(backupPath);
            var target = Normalize(gameManagedPath);
            if (!Directory.Exists(target)) throw new DirectoryNotFoundException(target);
            RejectLink(backup);
            RejectLink(target);
            var manifestPath = Path.Combine(backup, ManifestName);
            RejectLink(manifestPath);
            var manifest = JsonSerializer.Deserialize<BackupManifest>(File.ReadAllText(manifestPath))
                ?? throw new IOException("백업 메타데이터가 없습니다.");
            if (!string.Equals(Normalize(manifest.TargetPath), target, PathComparison) ||
                string.Equals(backup, target, PathComparison))
                throw new IOException("이 백업의 원래 게임 경로와 복원 대상이 다릅니다.");
            var restore = new Dictionary<string, byte[]?>();
            var filesToRestore = (manifest.ReplacedFiles != null && manifest.ReplacedFiles.Count > 0)
                ? manifest.ReplacedFiles
                : manifest.Files.Keys.ToList();

            foreach (var key in filesToRestore)
            {
                if (!manifest.Files.TryGetValue(key, out var expectedHash))
                    throw new IOException($"백업에 해당 파일이 없습니다: {key}");
                if (!IsFileName(key) || key == ManifestName) throw new IOException("잘못된 백업 파일명입니다.");
                var source = Path.Combine(backup, key);
                RejectLink(source);
                var bytes = File.ReadAllBytes(source);
                if (Convert.ToHexString(SHA256.HashData(bytes)) != expectedHash)
                    throw new IOException($"백업 무결성 검증 실패: {key}");
                restore.Add(key, bytes);
            }
            foreach (var name in manifest.AddedFiles)
            {
                if (!IsFileName(name) || !name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) || restore.ContainsKey(name))
                    throw new IOException("잘못된 추가 파일 기록입니다.");
                restore.Add(name, null);
            }
            foreach (var name in restore.Keys)
            {
                var destination = Path.Combine(target, name);
                RejectLink(destination);
                originals.Add(destination, File.Exists(destination) ? File.ReadAllBytes(destination) : null);
            }
            foreach (var entry in restore)
            {
                var destination = Path.Combine(target, entry.Key);
                attempted.Add(destination);
                if (entry.Value == null) File.Delete(destination);
                else File.WriteAllBytes(destination, entry.Value);
            }
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            foreach (var destination in attempted.AsEnumerable().Reverse())
            {
                try
                {
                    if (originals[destination] is { } bytes) File.WriteAllBytes(destination, bytes);
                    else File.Delete(destination);
                }
                catch (Exception rollbackError) { error += $"\n복원 취소 실패: {rollbackError.Message}"; }
            }
            return false;
        }
    }
}
