using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using BclToolApp.Models;

namespace BclToolApp.Services;

public class CrashLogAnalyzerService
{
    private static readonly Regex MissingMethodRegex = new(
        @"(?:System\.)?MissingMethodException:\s*(?:Method not found:)?\s*['""]?([^'""]+)['""]?",
        RegexOptions.Compiled | RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1));

    private static readonly Regex TypeLoadRegex = new(
        @"(?:System\.)?TypeLoadException:\s*(?:Could not resolve type with token [0-9a-fA-F]+ from typeref \(expected (?:class|type) ['""]?([^'""]+)['""]? in assembly ['""]?([^'""]+)['""]?\)|Could not (?:load|resolve) type ['""]?([^'""]+)['""]?\s*(?:from|in) assembly\s*['""]?([^'""]+)['""]?)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1));

    private static readonly Regex FileNotFoundRegex = new(
        @"(?:System\.IO\.)?(?:FileNotFoundException|FileLoadException):\s*(?:Could not load file or assembly\s*['""]?([^'""]+)['""]?)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1));

    private static readonly Regex BadImageRegex = new(
        @"(?:System\.)?BadImageFormatException:\s*([^\r\n]+)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1));

    private static readonly Regex NativeCrashRegex = new(
        @"(?:SIGSEGV|signal 11|Crash in libil2cpp\.so|Crash in libmono\.so|UnityPlayer\.dll!0x[0-9a-fA-F]+)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1));

    private static readonly Regex LoaderBootstrapRegex = new(
        @"\[MelonLoader\]\s*(?:Bootstrap Failure|Failed to load|Hook Failure|Failed to initialize)|\[BepInEx\]\s*Fatal|\[Fatal\s*:\s*BepInEx\]",
        RegexOptions.Compiled | RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1));

    private static readonly HashSet<string> ExcludedNuGetPackageAssemblies = new(StringComparer.OrdinalIgnoreCase)
    {
        "System.Runtime.CompilerServices.Unsafe",
        "System.Memory",
        "System.Buffers",
        "System.Numerics.Vectors",
        "System.Threading.Tasks.Extensions",
        "System.Text.Json",
        "System.Text.Encodings.Web"
    };

    public CrashDiagnosisResult Analyze(string logContent) => Analyze(logContent, null);

    public CrashDiagnosisResult Analyze(string logContent, string? gameManagedPath)
    {
        if (string.IsNullOrWhiteSpace(logContent))
        {
            return new CrashDiagnosisResult
            {
                Category = CrashCategory.NormalOrUnknown,
                Summary = "분석할 로그 내용이 없습니다.",
                DetailedExplanation = "로그 창에 MelonLoader/Player.log/logcat 내용을 붙여넣거나 파일을 열어주세요."
            };
        }

        string[] lines = logContent.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None);
        int crashLineIndex = -1;
        Match? matchedRegex = null;
        string matchedType = string.Empty;

        // 1. 순차적으로 최초의 결정적 예외 라인 탐색
        for (int i = 0; i < lines.Length; i++)
        {
            string line = lines[i];

            var mm = MissingMethodRegex.Match(line);
            if (mm.Success) { crashLineIndex = i; matchedRegex = mm; matchedType = "MissingMethodException"; break; }

            var tl = TypeLoadRegex.Match(line);
            if (tl.Success) { crashLineIndex = i; matchedRegex = tl; matchedType = "TypeLoadException"; break; }

            var fn = FileNotFoundRegex.Match(line);
            if (fn.Success) { crashLineIndex = i; matchedRegex = fn; matchedType = line.Contains("FileLoadException", StringComparison.OrdinalIgnoreCase) ? "FileLoadException" : "FileNotFoundException"; break; }

            var bi = BadImageRegex.Match(line);
            if (bi.Success) { crashLineIndex = i; matchedRegex = bi; matchedType = "BadImageFormatException"; break; }

            var nc = NativeCrashRegex.Match(line);
            if (nc.Success) { crashLineIndex = i; matchedRegex = nc; matchedType = "NativeCrash"; break; }

            var lb = LoaderBootstrapRegex.Match(line);
            if (lb.Success) { crashLineIndex = i; matchedRegex = lb; matchedType = "LoaderBootstrap"; break; }
        }

        if (crashLineIndex == -1)
        {
            return new CrashDiagnosisResult
            {
                Category = CrashCategory.NormalOrUnknown,
                Summary = "명시적인 BCL 결손 또는 관리형 크래시 시그니처가 검출되지 않았습니다.",
                DetailedExplanation = "로그 내에 MissingMethodException, TypeLoadException, FileNotFoundException 등의 명시적 관리형 오류 패턴이 발견되지 않았습니다.",
                Recommendation = "로그 전체를 확인하거나 크래시 직전 모드로더가 멈춘 마지막 콘솔 출력 라인을 확인해 보세요.",
                ExtractedContext = ExtractContext(lines, Math.Max(0, lines.Length - 30), lines.Length - 1)
            };
        }

        // 앞뒤 25줄 문맥 추출
        int startLine = Math.Max(0, crashLineIndex - 15);
        int endLine = Math.Min(lines.Length - 1, crashLineIndex + 25);
        string context = ExtractContext(lines, startLine, endLine);

        // 2. 예외 세부 정보 및 카테고리 판정
        var result = ClassifyCrash(matchedType, matchedRegex!, lines[crashLineIndex], context);
        if (result.IsBclTransplantCandidate && !string.IsNullOrWhiteSpace(gameManagedPath) && Directory.Exists(gameManagedPath))
        {
            CheckGameProfileCompatibility(result, gameManagedPath);
        }
        return result;
    }

    private CrashDiagnosisResult ClassifyCrash(string matchedType, Match match, string fullLine, string context)
    {
        var result = new CrashDiagnosisResult
        {
            ExceptionType = matchedType,
            ExtractedContext = context
        };

        if (matchedType == "MissingMethodException")
        {
            string member = match.Groups[1].Value.Trim();
            result.TargetMember = member;

            if (fullLine.Contains("in the il2cpp runtime", StringComparison.OrdinalIgnoreCase))
            {
                result.Category = CrashCategory.NativeOrJniIssue;
                result.Summary = $"IL2CPP 런타임 심볼 누락: {member}";
                result.DetailedExplanation = fullLine;
                result.Recommendation = "• IL2CPP 네이티브 런타임에 등록되지 않은 icall입니다. Managed BCL 교체로는 해결할 수 없습니다.";
            }
            else if (IsGameSpecificMember(member))
            {
                result.Category = CrashCategory.GameCodeStripping;
                result.Summary = $"게임 전용 코드 스트리핑 감지: {member}";
                result.DetailedExplanation = $"게임 어셈블리(Assembly-CSharp 등)의 메서드가 스트리핑되어 발생한 오류입니다. 이는 BCL(Base Class Library) 결손이 아닙니다.";
                result.Recommendation = "⛔ BCL DLL을 복사/이식해도 해결되지 않습니다!\n• 모드 코드에서 해당 게임 메서드를 직접 호출하지 않도록 우회(리플렉션 사전 검사 또는 대안 API 사용)해야 합니다.\n• Unity link.xml 또는 il2cpp 스트리핑 보존 설정을 점검해야 합니다.";
            }
            else
            {
                result.Category = CrashCategory.BclStrippedApi;
                result.Summary = $"BCL 핵심 메서드 스트리핑 누락: {member}";
                result.DetailedExplanation = $"모드로더 또는 모드가 호출하려는 표준 BCL API(예: System.Linq, System.Reflection)가 게임의 스트리핑된 BCL에 존재하지 않습니다.";
                result.Recommendation = "✅ BCL 이식 대상입니다!\n• 호환되는 표준 Mono / .NET Standard BCL 어셈블리(System.Core.dll 등)를 게임의 Managed 디렉토리에 보충/이식하십시오.\n• 주의: mscorlib 등 핵심 런타임 어셈블리의 경우 버전 ABI가 충돌하지 않는 호환본을 선택해야 합니다.";
            }
        }
        else if (matchedType == "TypeLoadException")
        {
            string typeName = (!string.IsNullOrEmpty(match.Groups[1].Value) ? match.Groups[1].Value : match.Groups[3].Value).Trim();
            string assemblyName = (!string.IsNullOrEmpty(match.Groups[2].Value) ? match.Groups[2].Value : match.Groups[4].Value).Trim();
            result.TargetMember = typeName;
            result.TargetAssembly = assemblyName;

            if (IsGameSpecificMember(typeName) || IsGameSpecificMember(assemblyName))
            {
                result.Category = CrashCategory.GameCodeStripping;
                result.Summary = $"게임 전용 타입 스트리핑 감지: {typeName}";
                result.DetailedExplanation = $"게임 내부 타입({typeName})이 IL2CPP/Unity 빌드 시 제거(스트리핑)되었습니다.";
                result.Recommendation = "⛔ BCL 이식 대상이 아닙니다!\n• 게임 바이너리 자체에서 심볼이 날아간 것이므로 BCL DLL 교체로는 해결할 수 없습니다.";
            }
            else
            {
                result.Category = CrashCategory.BclStrippedApi;
                result.Summary = $"BCL 타입 누락 (TypeLoadException): {typeName}";
                result.DetailedExplanation = $"BCL 어셈블리({assemblyName}) 내에 기대되는 타입({typeName})이 누락되었습니다. 런타임 프로파일(예: netstandard2.0 vs .NET 4.x) 불일치일 수 있습니다.";
                result.Recommendation = "✅ BCL 이식 또는 프로파일 정렬 대상입니다!\n• 대상 어셈블리의 누락 타입을 포함한 정품/완전한 BCL 어셈블리로 교체하거나 모드로더의 의존성 프로파일을 맞추세요.";
            }
        }
        else if (matchedType == "FileNotFoundException")
        {
            string asmName = match.Groups[1].Value.Trim();
            result.TargetAssembly = asmName;

            if (IsGameSpecificMember(asmName))
            {
                result.Category = CrashCategory.GameCodeStripping;
                result.Summary = $"게임 또는 모드 어셈블리 누락: {asmName}";
                result.DetailedExplanation = $"필요한 플러그인 또는 게임 자체 어셈블리 파일을 찾을 수 없습니다.";
                result.Recommendation = "• 필요한 모드/플러그인 어셈블리가 제대로 배치되었는지 확인하세요.";
            }
            else
            {
                result.Category = CrashCategory.BclMissingAssembly;
                result.Summary = $"필수 BCL 어셈블리 파일 부재: {asmName}";
                result.DetailedExplanation = $"모드로더 부팅 중 필수 BCL 파일({asmName})을 로드하지 못해 중단되었습니다.";
                result.Recommendation = "✅ BCL 이식 우선 대상입니다!\n• Donor BCL 세트에서 해당 어셈블리(예: System.Core.dll, System.Data.dll 등)를 복사하여 게임 Managed 폴더에 투입하세요.";
            }
        }
        else if (matchedType == "FileLoadException")
        {
            result.Category = CrashCategory.NormalOrUnknown;
            result.TargetAssembly = match.Groups[1].Value.Trim();
            result.Summary = "어셈블리 로드 실패: 파일 누락으로 단정할 수 없습니다.";
            result.DetailedExplanation = fullLine;
            result.Recommendation = "버전, 서명, 의존성 및 로더 설정을 확인하세요.";
        }
        else if (matchedType == "BadImageFormatException")
        {
            result.Category = CrashCategory.InteropAbiMismatch;
            result.Summary = "어셈블리 포맷 / 비트(x86 vs x64) 불일치";
            result.DetailedExplanation = fullLine;
            result.Recommendation = "• 어셈블리 아키텍처가 32비트/64비트로 엇갈렸거나 바이너리가 손상되었을 가능성이 있습니다. 타겟 플랫폼을 확인하세요.";
        }
        else if (matchedType == "NativeCrash")
        {
            result.Category = CrashCategory.NativeOrJniIssue;
            result.Summary = "Native / IL2CPP 엔진 크래시 (SIGSEGV / Native Library Crash)";
            result.DetailedExplanation = fullLine;
            result.Recommendation = "• 관리형 BCL 문제가 아닌 네이티브 레벨(libil2cpp, libmono, JNI)의 메모리 침범 또는 심볼 누락입니다. 메타데이터 버전(global-metadata.dat) 또는 네이티브 후킹 로직을 점검하세요.";
        }
        else if (matchedType == "LoaderBootstrap")
        {
            result.Category = CrashCategory.LoaderBootstrap;
            result.Summary = "모드로더 자체 부트스트랩 실패";
            result.DetailedExplanation = fullLine;
            result.Recommendation = "• MelonLoader / BepInEx의 버전이 대상 Unity 런타임 버전과 호환되는지 확인하세요.";
        }

        if (result.IsBclTransplantCandidate && !HasBclEvidence(result))
        {
            result.Category = CrashCategory.NormalOrUnknown;
            result.Summary = "BCL 결손 여부를 확인할 수 없는 의존성 오류입니다.";
            result.DetailedExplanation = fullLine;
            result.Recommendation = "타입을 선언한 어셈블리와 실제 로드된 버전을 확인하세요. 이 로그만으로 BCL 이식을 권장하지 않습니다.";
        }
        else if (result.IsBclTransplantCandidate)
        {
            result.Summary = "BCL API 또는 의존성 불일치 가능성: " +
                (string.IsNullOrEmpty(result.TargetMember) ? result.TargetAssembly : result.TargetMember);
            result.DetailedExplanation = "로그 패턴에 따른 추정입니다. 스트리핑, 버전 불일치, 의존성 로드 실패를 추가로 구분해야 합니다.";
            result.Recommendation = "대상 런타임과 어셈블리의 API를 비교한 후 이식을 검토하세요. IL2CPP의 AOT 컴파일된 게임 코드는 Managed DLL 복사로 복구되지 않습니다.";
        }
        return result;
    }

    private static bool HasBclEvidence(CrashDiagnosisResult result)
    {
        if (!string.IsNullOrWhiteSpace(result.TargetAssembly))
        {
            if (Regex.IsMatch(result.TargetAssembly, @"Version=[5-9]\.", RegexOptions.IgnoreCase))
                return false;

            var name = result.TargetAssembly.Split(',')[0].Trim();
            if (ExcludedNuGetPackageAssemblies.Contains(name))
                return false;

            return name.Equals("mscorlib", StringComparison.OrdinalIgnoreCase) ||
                   name.Equals("netstandard", StringComparison.OrdinalIgnoreCase) ||
                   name.Equals("System", StringComparison.OrdinalIgnoreCase) ||
                   name.StartsWith("System.", StringComparison.Ordinal);
        }
        // Return and parameter types do not identify the declaring assembly.
        var member = result.TargetMember.Split('(')[0].Trim();
        member = member[(member.LastIndexOf(' ') + 1)..];
        return member.StartsWith("System.", StringComparison.Ordinal);
    }

    private static bool IsGameSpecificMember(string name)
    {
        if (string.IsNullOrEmpty(name)) return false;
        string lower = name.ToLowerInvariant();
        if (lower.Contains("videoplayer") || lower.Contains("unityplayer") || lower.StartsWith("unityengine") || lower.Contains("::"))
            return false;

        return lower.Contains("assembly-csharp") ||
               lower.Contains("game") && !lower.Contains("system") ||
               (lower.Contains("player") && !lower.Contains("videoplayer") && !lower.Contains("unityplayer")) ||
               lower.Contains("character") ||
               lower.Contains("controller") ||
               lower.Contains("ui_") ||
               lower.Contains("stage");
    }

    private static void CheckGameProfileCompatibility(CrashDiagnosisResult result, string gameManagedPath)
    {
        try
        {
            var mscorlibPath = Path.Combine(gameManagedPath, "mscorlib.dll");
            if (File.Exists(mscorlibPath))
            {
                using var stream = File.OpenRead(mscorlibPath);
                using var asm = Mono.Cecil.AssemblyDefinition.ReadAssembly(stream);
                if (asm.Name.Version.Major < 4)
                {
                    if (result.TargetMember.Contains("Array.Empty", StringComparison.OrdinalIgnoreCase))
                    {
                        result.Category = CrashCategory.NormalOrUnknown;
                        result.Summary = $"BCL 프로파일 미지원 API (게임 BCL 버전 {asm.Name.Version}): {result.TargetMember}";
                        result.DetailedExplanation = $"게임이 .NET 2.0/3.5 프로파일(mscorlib v{asm.Name.Version})을 사용하고 있어, 상위 .NET 4.6+ 전용 API({result.TargetMember})를 지원하지 않습니다. 이는 BCL 스트리핑이 아니므로 BCL 이식으로 해결할 수 없습니다.";
                        result.Recommendation = "• 모드가 대상 게임의 .NET 프로파일과 호환되지 않는 최신 API를 호출하고 있습니다. 모드 버전을 낮추거나 대상 프로파일에 맞게 재컴파일해야 합니다.";
                    }
                }
            }
        }
        catch { }
    }

    private static string ExtractContext(string[] lines, int start, int end)
    {
        var sb = new System.Text.StringBuilder();
        for (int i = start; i <= end && i < lines.Length; i++)
        {
            sb.AppendLine(lines[i]);
        }
        return sb.ToString();
    }
}

