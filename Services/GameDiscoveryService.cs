using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Text.RegularExpressions;
using BclToolApp.Models;
using Microsoft.Win32;

namespace BclToolApp.Services;

public sealed class GameDiscoveryService
{
    public sealed class DiscoveryResult
    {
        public List<InstalledGame> Games { get; } = new();
        public List<string> Warnings { get; } = new();
    }

    public IEnumerable<string> FindSteamRoots()
    {
        var roots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (OperatingSystem.IsWindows())
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam");
            if (key?.GetValue("SteamPath") is string path && Directory.Exists(path)) roots.Add(path);
        }
        foreach (var drive in DriveInfo.GetDrives().Where(d => d.IsReady && d.DriveType == DriveType.Fixed))
            foreach (var name in new[] { "Steam", "SteamLibrary", @"Program Files (x86)\Steam", @"Program Files\Steam" })
            {
                var path = Path.Combine(drive.RootDirectory.FullName, name);
                if (Directory.Exists(Path.Combine(path, "steamapps"))) roots.Add(path);
            }
        return roots;
    }

    public DiscoveryResult Discover(IEnumerable<string> roots, CancellationToken cancellationToken = default)
    {
        var result = new DiscoveryResult();
        var libraries = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in roots)
        {
            try
            {
                var path = Path.TrimEndingDirectorySeparator(Path.GetFullPath(raw.Trim().Trim('"')));
                if (!Directory.Exists(path)) throw new DirectoryNotFoundException(path);
                libraries.Add(path);
                var vdf = Path.Combine(path, "steamapps", "libraryfolders.vdf");
                if (File.Exists(vdf))
                    foreach (Match match in Regex.Matches(File.ReadAllText(vdf), "\"path\"\\s*\"((?:\\\\.|[^\"\\\\])*)\"", RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1)))
                        libraries.Add(match.Groups[1].Value.Replace(@"\\", @"\"));
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { result.Warnings.Add($"검색 경로 확인 실패: {ex.Message}"); }
        }
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var library in libraries)
        {
            try
            {
                var common = Directory.Exists(Path.Combine(library, "steamapps", "common")) ? Path.Combine(library, "steamapps", "common")
                    : Directory.Exists(Path.Combine(library, "common")) ? Path.Combine(library, "common") : library;
                var isLibrary = !string.Equals(common, library, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(Path.GetFileName(common), "common", StringComparison.OrdinalIgnoreCase);
                var direct = isLibrary ? null : InspectGame(common);
                var paths = direct != null ? new[] { common } : Directory.GetDirectories(common);
                foreach (var path in paths)
                {
                    try
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        if (!seen.Add(Path.GetFullPath(path))) continue;
                        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                        {
                            result.Warnings.Add($"{Path.GetFileName(path)}: Junction 또는 심볼릭 링크 폴더는 건너뜁니다.");
                            continue;
                        }
                        var game = InspectGame(path);
                        if (game != null) result.Games.Add(game);
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex) { result.Warnings.Add($"{Path.GetFileName(path)}: {ex.Message}"); }
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { result.Warnings.Add($"{library}: {ex.Message}"); }
        }
        result.Games.Sort((a, b) => StringComparer.OrdinalIgnoreCase.Compare(a.Name, b.Name));
        return result;
    }

    /// <summary>Turns whatever the user pasted — game folder, *_Data, Managed, the .exe, a Unity editor
    /// profile — into the folder that actually holds the BCL. Quotes and trailing separators are tolerated.</summary>
    public static (string? Path, string Note) ResolveBclDirectory(string? input)
    {
        if (string.IsNullOrWhiteSpace(input)) return (null, "경로를 입력하세요.");
        string path;
        try
        {
            path = Path.TrimEndingDirectorySeparator(Path.GetFullPath(input.Trim().Trim('"')));
            if (File.Exists(path)) path = Path.GetDirectoryName(path) ?? path;
            if (!Directory.Exists(path)) return (null, $"폴더가 존재하지 않습니다: {path}");
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return (null, $"경로 형식이 잘못되었습니다: {ex.Message}");
        }
        try
        {
            // Already a BCL folder (game Managed, or a Unity editor mono profile).
            if (File.Exists(Path.Combine(path, "mscorlib.dll")) ||
                string.Equals(Path.GetFileName(path), "Managed", StringComparison.OrdinalIgnoreCase))
                return (path, string.Empty);
            var managed = Path.Combine(path, "Managed");
            if (Directory.Exists(managed)) return (managed, $"{Path.GetFileName(path)}\\Managed 폴더로 인식했습니다.");

            var candidates = Directory.GetDirectories(path, "*_Data")
                .Select(d => Path.Combine(d, "Managed")).Where(Directory.Exists).OrderBy(d => d).ToArray();
            if (candidates.Length == 1)
                return (candidates[0], $"게임 폴더에서 Managed 폴더를 찾았습니다: ...\\{Path.GetFileName(Path.GetDirectoryName(candidates[0])!)}\\Managed");
            if (candidates.Length > 1)
            {
                var exe = Directory.GetFiles(path, "*.exe").Select(Path.GetFileNameWithoutExtension).ToHashSet(StringComparer.OrdinalIgnoreCase);
                var match = candidates.FirstOrDefault(c =>
                    exe.Contains(Path.GetFileName(Path.GetDirectoryName(c)!).Replace("_Data", string.Empty)));
                return match != null
                    ? (match, $"Managed 후보가 여러 개라 실행 파일과 같은 이름을 골랐습니다: {match}")
                    : (null, "Managed 후보가 여러 개입니다. 사용할 폴더를 직접 지정하세요: " + string.Join(" / ", candidates));
            }
            if (File.Exists(Path.Combine(path, "GameAssembly.dll")) ||
                Directory.GetDirectories(path, "*_Data").Any(d => File.Exists(Path.Combine(d, "il2cpp_data", "Metadata", "global-metadata.dat"))))
                return (null, "IL2CPP 게임입니다. Managed DLL 이식으로는 복구할 수 없습니다.");
            // A folder of assemblies without mscorlib is still usable (a partial BCL set the user extracted).
            if (Directory.GetFiles(path, "*.dll").Length > 0)
                return (path, "mscorlib.dll이 없는 폴더입니다. BCL 폴더가 맞는지 확인하세요.");
            return (null, "이 폴더에서 Managed(BCL) 폴더를 찾지 못했습니다. 게임 설치 폴더나 Managed 폴더를 지정하세요.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return (null, $"폴더를 읽을 수 없습니다: {ex.Message}");
        }
    }

    public InstalledGame? InspectGame(string path)
    {
        if (!Directory.Exists(path) || (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) return null;
        var data = Directory.GetDirectories(path, "*_Data")
            .Where(d => (File.GetAttributes(d) & FileAttributes.ReparsePoint) == 0).OrderBy(d => d).ToArray();
        if (data.Length == 0 && !File.Exists(Path.Combine(path, "UnityPlayer.dll"))) return null;
        var managed = data.Select(d => Path.Combine(d, "Managed")).Where(Directory.Exists).ToArray();
        bool il2cpp = File.Exists(Path.Combine(path, "GameAssembly.dll")) ||
            data.Any(d => File.Exists(Path.Combine(d, "il2cpp_data", "Metadata", "global-metadata.dat")));
        var loaders = new[] { "MelonLoader", "BepInEx" }.Where(n => Directory.Exists(Path.Combine(path, n))).ToArray();
        var logs = new List<string>();
        foreach (var relative in new[] { "MelonLoader/Latest.log", "BepInEx/LogOutput.log", "Player.log" })
            if (File.Exists(Path.Combine(path, relative))) logs.Add(Path.Combine(path, relative));
        var logDirectory = Path.Combine(path, "MelonLoader", "Logs");
        if (Directory.Exists(logDirectory)) logs.AddRange(Directory.GetFiles(logDirectory, "*.log"));

        foreach (var d in data)
        {
            var appInfoPath = Path.Combine(d, "app.info");
            if (File.Exists(appInfoPath))
            {
                try
                {
                    var lines = File.ReadAllLines(appInfoPath).Where(l => !string.IsNullOrWhiteSpace(l)).Select(l => l.Trim()).ToArray();
                    if (lines.Length >= 2)
                    {
                        var companyName = lines[0];
                        var productName = lines[1];
                        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                        var localLow = Path.Combine(Path.GetDirectoryName(localAppData)!, "LocalLow");
                        var localLowPlayerLog = Path.Combine(localLow, companyName, productName, "Player.log");
                        if (File.Exists(localLowPlayerLog))
                        {
                            logs.Add(localLowPlayerLog);
                        }
                    }
                }
                catch { }
            }
        }

        return new InstalledGame
        {
            Name = Path.GetFileName(Path.TrimEndingDirectorySeparator(path)), InstallPath = Path.GetFullPath(path),
            ManagedPath = managed.Length == 1 ? managed[0] : string.Empty,
            Runtime = il2cpp ? "IL2CPP" : managed.Length == 1 ? "Mono" : managed.Length > 1 ? "Managed 후보 여러 개" : "Unity / 런타임 미확인",
            Loader = loaders.Length == 0 ? "미검출" : string.Join(" + ", loaders),
            LogPath = logs.OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault() ?? string.Empty
        };
    }

    public string ReadLogTail(string path, int maxCharacters = 200_000)
    {
        if (maxCharacters <= 0) throw new ArgumentOutOfRangeException(nameof(maxCharacters));
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        var tail = new System.Text.StringBuilder();
        var buffer = new char[8192];
        int count;
        while ((count = reader.Read(buffer, 0, buffer.Length)) > 0)
        {
            tail.Append(buffer, 0, count);
            if (tail.Length > maxCharacters) tail.Remove(0, tail.Length - maxCharacters);
        }
        return tail.ToString();
    }
}



