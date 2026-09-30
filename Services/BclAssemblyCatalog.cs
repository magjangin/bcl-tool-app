using System;
using System.Collections.Generic;
using System.IO;

namespace BclToolApp.Services;

/// <summary>Which DLL names in a Managed folder are Unity's Mono BCL, which are the game's own code, and
/// which are NuGet packages a game or mod ships. An allowlist, not a "System.*" rule: a donor game's
/// System.Text.Json.dll or Microsoft.Extensions.*.dll is its own dependency, never part of the BCL.</summary>
public static class BclAssemblyCatalog
{
    /// <summary>The core BCL: worth adding to the game when the donor has it and the game does not.</summary>
    private static readonly HashSet<string> Critical = new(StringComparer.OrdinalIgnoreCase)
    {
        "mscorlib.dll", "netstandard.dll", "System.dll", "System.Core.dll", "System.Xml.dll",
        "System.Data.dll", "System.Numerics.dll", "System.Runtime.Serialization.dll",
        "System.IO.Compression.dll", "System.Configuration.dll", "System.Xml.Linq.dll", "Mono.Security.dll"
    };

    /// <summary>The rest of the Mono class library Unity ships in Managed/ (profiles differ per game).</summary>
    private static readonly HashSet<string> Extended = new(StringComparer.OrdinalIgnoreCase)
    {
        "System.ComponentModel.Composition.dll", "System.ComponentModel.DataAnnotations.dll",
        "System.Data.DataSetExtensions.dll", "System.Drawing.dll", "System.EnterpriseServices.dll",
        "System.IO.Compression.FileSystem.dll", "System.Net.Http.dll", "System.Net.Http.WebRequest.dll",
        "System.Runtime.Serialization.Formatters.Soap.dll", "System.Security.dll",
        "System.ServiceModel.Internals.dll", "System.Transactions.dll", "System.Web.dll",
        "System.Windows.Forms.dll", "System.Xml.Serialization.dll", "Microsoft.CSharp.dll",
        "Mono.CompilerServices.SymbolWriter.dll", "Mono.Data.Tds.dll", "Mono.Posix.dll",
        "Mono.WebBrowser.dll", "Boo.Lang.dll", "UnityScript.Lang.dll"
    };

    /// <summary>NuGet assemblies whose names look like BCL. The mod or game that needs them ships them.</summary>
    private static readonly HashSet<string> NuGetPackages = new(StringComparer.OrdinalIgnoreCase)
    {
        "System.Runtime.CompilerServices.Unsafe.dll", "System.Memory.dll", "System.Buffers.dll",
        "System.Numerics.Vectors.dll", "System.Threading.Tasks.Extensions.dll", "System.Text.Json.dll",
        "System.Text.Encodings.Web.dll", "System.ValueTuple.dll", "System.Collections.Immutable.dll",
        "System.Reflection.Metadata.dll", "System.IO.Pipelines.dll", "System.IO.Hashing.dll",
        "System.Diagnostics.DiagnosticSource.dll", "System.Runtime.dll",
        "System.Runtime.InteropServices.RuntimeInformation.dll"
    };

    public static bool IsNuGetPackage(string fileName) => NuGetPackages.Contains(fileName);

    public static bool IsCritical(string fileName) => Critical.Contains(fileName);

    /// <summary>True for Unity's own Mono BCL, including the I18N.* satellites.</summary>
    public static bool IsBcl(string fileName) =>
        !NuGetPackages.Contains(fileName) &&
        (Critical.Contains(fileName) || Extended.Contains(fileName) ||
         fileName.StartsWith("I18N", StringComparison.OrdinalIgnoreCase) && fileName.EndsWith(".dll", StringComparison.OrdinalIgnoreCase));

    /// <summary>Game code and engine assemblies. Copying a donor's copy of these swaps in another game.</summary>
    public static bool IsGameOrEngineCode(string fileName) =>
        fileName.StartsWith("Assembly-CSharp", StringComparison.OrdinalIgnoreCase) ||
        fileName.StartsWith("Assembly-UnityScript", StringComparison.OrdinalIgnoreCase) ||
        fileName.StartsWith("UnityEngine", StringComparison.OrdinalIgnoreCase) ||
        fileName.StartsWith("UnityEditor", StringComparison.OrdinalIgnoreCase) ||
        fileName.StartsWith("Unity.", StringComparison.OrdinalIgnoreCase);

    public static string Describe(string fileName) =>
        IsGameOrEngineCode(fileName) ? "게임/엔진 코드" :
        IsNuGetPackage(fileName) ? "NuGet 패키지(모드가 동봉)" :
        IsCritical(fileName) ? "핵심 BCL" :
        IsBcl(fileName) ? "BCL" : "기타 라이브러리";

    public static string FormatSize(long bytes) => bytes <= 0 ? "-" : $"{bytes:N0}B";

    public static long SizeOf(string directory, string fileName)
    {
        try
        {
            var info = new FileInfo(Path.Combine(directory, fileName));
            return info.Exists ? info.Length : 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return 0; }
    }
}
