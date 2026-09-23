using BclToolApp.Models;
using BclToolApp.Services;
using Xunit;

namespace BclToolApp.Tests;

public class CrashLogAnalyzerTests
{
    private readonly CrashLogAnalyzerService _analyzer = new();

    [Fact]
    public void Should_Detect_BclStrippedApi_When_MissingMethod_In_System()
    {
        string log = @"
[MelonLoader] Loading...
System.MissingMethodException: Method not found: 'System.Collections.Generic.IEnumerable`1<!!0> System.Linq.Enumerable.Select(System.Collections.Generic.IEnumerable`1<!!0>,System.Func`2<!!0,!!1>)'
  at MelonLoader.Core.Initialize () [0x00021] in <d48d1e345>:0";

        var result = _analyzer.Analyze(log);

        Assert.Equal(CrashCategory.BclStrippedApi, result.Category);
        Assert.True(result.IsBclTransplantCandidate);
        Assert.Contains("Select", result.TargetMember);
    }

    [Fact]
    public void Should_Detect_GameCodeStripping_When_Member_Is_In_AssemblyCSharp()
    {
        string log = @"
[CustomMod] Calling player code...
System.TypeLoadException: Could not load type 'Assembly-CSharp.PlayerCharacterController' from assembly 'Assembly-CSharp, Version=0.0.0.0'.
  at CustomMod.Core.HookPlayer () [0x00010]";

        var result = _analyzer.Analyze(log);

        Assert.Equal(CrashCategory.GameCodeStripping, result.Category);
        Assert.False(result.IsBclTransplantCandidate);
    }

    [Fact]
    public void Should_Detect_BclMissingAssembly_When_SystemCore_FileNotFound()
    {
        string log = @"
System.IO.FileNotFoundException: Could not load file or assembly 'System.Core, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b77a5c561934e089'
  at ModLoader.Bootstrap ()";

        var result = _analyzer.Analyze(log);

        Assert.Equal(CrashCategory.BclMissingAssembly, result.Category);
        Assert.True(result.IsBclTransplantCandidate);
        Assert.Contains("System.Core", result.TargetAssembly);
    }
}