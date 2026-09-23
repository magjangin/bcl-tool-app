using BclToolApp.Models;
using BclToolApp.Services;

namespace BclToolApp.Tests;

public class DiagnosticRegressionTests
{
    [Theory]
    [InlineData("", CrashCategory.NormalOrUnknown)]
    [InlineData("Loaded runtime: libmono.so", CrashCategory.NormalOrUnknown)]
    [InlineData("System.TypeLoadException: Something went wrong", CrashCategory.NormalOrUnknown)]
    [InlineData("System.IO.FileNotFoundException: Could not load file or assembly 'Newtonsoft.Json, Version=1.0.0.0'", CrashCategory.NormalOrUnknown)]
    [InlineData("System.MissingMethodException: Method not found: 'System.String Acme.Widget.Run()'", CrashCategory.NormalOrUnknown)]
    [InlineData("System.IO.FileLoadException: Could not load file or assembly 'System.Core, Version=1.0.0.0'", CrashCategory.NormalOrUnknown)]
    [InlineData("SIGSEGV", CrashCategory.NativeOrJniIssue)]
    [InlineData("System.BadImageFormatException: wrong architecture", CrashCategory.InteropAbiMismatch)]
    [InlineData("[MelonLoader] Bootstrap Failure", CrashCategory.LoaderBootstrap)]
    public void ClassifiesWithoutRecommendingUnrelatedBclCopies(string log, CrashCategory expected)
    {
        var result = new CrashLogAnalyzerService().Analyze(log);
        Assert.Equal(expected, result.Category);
        Assert.False(result.IsBclTransplantCandidate);
    }

    [Fact]
    public void PreservesFileLoadExceptionType()
    {
        var result = new CrashLogAnalyzerService().Analyze("System.IO.FileLoadException: Could not load file or assembly 'System.Core'");
        Assert.Equal("FileLoadException", result.ExceptionType);
    }

    [Fact]
    public void ContextIncludesFirstExceptionAndNearbyLines()
    {
        var result = new CrashLogAnalyzerService().Analyze("before\r\nSIGSEGV\r\nafter");
        Assert.Contains("before", result.ExtractedContext);
        Assert.Contains("after", result.ExtractedContext);
    }
}
