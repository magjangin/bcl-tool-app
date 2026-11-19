using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Data;
using Avalonia.Headless;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;
using BclToolApp.Models;
using BclToolApp.Services;
using BclToolApp.ViewModels;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace BclToolApp.Tests;

public class HeadlessTestApp : Application
{
    public override void Initialize() => Styles.Add(new FluentTheme());
}

// Each test asserts the correct behaviour, so a failure reproduces a known bug.
// Log lines have the same shape as real MelonLoader/BepInEx logs found under H:\steam.
public sealed class BugReproTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "BclBugRepro", Guid.NewGuid().ToString("N"));
    private readonly CrashLogAnalyzerService analyzer = new();
    public BugReproTests() => Directory.CreateDirectory(root);

    private static void Dll(string folder, string name, int version = 4, bool referenceAssembly = false)
    {
        Directory.CreateDirectory(folder);
        var assemblyName = Path.GetFileNameWithoutExtension(name);
        // Cecil cannot build a module named mscorlib directly; rename it just before writing.
        using var asm = AssemblyDefinition.CreateAssembly(new AssemblyNameDefinition("Fixture." + assemblyName, new Version(version, 0, 0, 0)), name, ModuleKind.Dll);
        var module = asm.MainModule;
        var type = new TypeDefinition("Example", "T", TypeAttributes.Public, module.TypeSystem.Object);
        module.Types.Add(type);
        var method = new MethodDefinition("M", MethodAttributes.Public | MethodAttributes.Static, module.TypeSystem.Void);
        method.Body.Instructions.Add(Instruction.Create(OpCodes.Ret));
        type.Methods.Add(method);
        if (referenceAssembly)
        {
            var attribute = new TypeReference("System.Runtime.CompilerServices", "ReferenceAssemblyAttribute", module, module.TypeSystem.CoreLibrary);
            asm.CustomAttributes.Add(new CustomAttribute(new MethodReference(".ctor", module.TypeSystem.Void, attribute) { HasThis = true }));
        }
        asm.Name.Name = assemblyName;
        asm.Write(Path.Combine(folder, name));
    }

    // ---------- Crash log analyzer ----------

    [Fact]
    public void MonoTypeLoadTokenFormatForStrippedBclTypeIsTransplantCandidate()
    {
        var result = analyzer.Analyze("[11:15:17.867] [ERROR] [Mod] System.TypeLoadException: Could not resolve type with token 01000015 from typeref (expected class 'System.Linq.Expressions.Expression' in assembly 'System.Core, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b77a5c561934e089')");
        Assert.True(result.IsBclTransplantCandidate, $"Category={result.Category}, Member='{result.TargetMember}', Assembly='{result.TargetAssembly}'");
        Assert.Contains("System.Core", result.TargetAssembly);
    }

    [Fact]
    public void QuotedTypeLoadFormatForStrippedBclTypeIsTransplantCandidate()
    {
        var result = analyzer.Analyze("TypeLoadException: Could not load type 'System.Linq.Expressions.Expression' from assembly 'System.Core, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b77a5c561934e089'.");
        Assert.True(result.IsBclTransplantCandidate);
    }

    [Fact]
    public void ModBuiltForNet6IsNotTransplantCandidate()
    {
        // Real line (Fun with Ragdolls Plus): the type only exists in .NET 6, not in any Mono BCL.
        var result = analyzer.Analyze("[11:15:17.867] [ERROR] [SkeletonLogger] System.TypeLoadException: Could not resolve type with token 01000015 from typeref (expected class 'System.Runtime.CompilerServices.DefaultInterpolatedStringHandler' in assembly 'System.Runtime, Version=6.0.0.0, Culture=neutral, PublicKeyToken=b03f5f7f11d50a3a')");
        Assert.False(result.IsBclTransplantCandidate);
    }

    [Theory]
    [InlineData("10.0.0.0")]
    [InlineData("12.0.0.0")]
    public void ModBuiltForNet10OrHigherIsNotTransplantCandidate(string version)
    {
        var result = analyzer.Analyze($"System.TypeLoadException: Could not load type 'System.Runtime.CompilerServices.Unsafe' from assembly 'System.Runtime, Version={version}, Culture=neutral, PublicKeyToken=b03f5f7f11d50a3a'.");
        Assert.False(result.IsBclTransplantCandidate);
    }

    [Fact]
    public void BenignEarlierExceptionDoesNotMaskLaterBclCrash()
    {
        var log =
            "[00:00:01.000] [WARN] System.IO.FileLoadException: Could not load file or assembly 'OptionalPlugin, Version=1.0.0.0'\n" +
            "[00:00:02.000] [ERROR] System.TypeLoadException: VTable setup of type System.Reflection.DelegatingTypeInfo failed\n";
        var result = analyzer.Analyze(log);
        Assert.True(result.IsBclTransplantCandidate, $"Category={result.Category}, Summary={result.Summary}");
        Assert.Equal("System.Reflection.DelegatingTypeInfo", result.TargetMember);
    }

    [Theory]
    // Real MelonLoader failure on a stripped mscorlib.
    [InlineData("[12:00:00.000] [ERROR] System.TypeLoadException: VTable setup of type System.Reflection.DelegatingTypeInfo failed")]
    // Mono also nests it inside a field load failure.
    [InlineData("System.TypeLoadException: Could not load type of field 'Mod.Patcher:info' (0) due to: VTable setup of type System.Reflection.DelegatingTypeInfo failed assembly:Mod.dll type:Patcher member:(null)")]
    public void VTableSetupFailureOfReflectionTypeIsTransplantCandidate(string log)
    {
        var result = analyzer.Analyze(log);
        Assert.True(result.IsBclTransplantCandidate, $"Category={result.Category}, Member='{result.TargetMember}'");
        Assert.Equal("System.Reflection.DelegatingTypeInfo", result.TargetMember);
        Assert.Contains("VTable", result.ExceptionType);
        Assert.Contains("GetPEKind", result.Recommendation);
    }

    [Fact]
    public void VTableSetupFailureOfThirdPartyTypeIsNotTransplantCandidate()
    {
        // Shape of the In Falsus Demo case: a stripped third-party ZString.dll, not the BCL.
        var result = analyzer.Analyze("System.TypeLoadException: VTable setup of type Cysharp.Text.Utf16ValueStringBuilder failed");
        Assert.False(result.IsBclTransplantCandidate, $"Category={result.Category}");
        Assert.Equal("Cysharp.Text.Utf16ValueStringBuilder", result.TargetMember);
    }

    [Fact]
    public async Task ApiMissingFromGameProfileIsNotTreatedAsStripping()
    {
        // Real case (Cuphead): mscorlib 2.0.0.0 never had Array.Empty (.NET 4.6+), so copying BCL cannot fix it.
        var game = Path.Combine(root, "steamapps", "common", "Cuphead");
        Dll(Path.Combine(game, "Cuphead_Data", "Managed"), "mscorlib.dll", version: 2);
        Directory.CreateDirectory(Path.Combine(game, "MelonLoader"));
        File.WriteAllText(Path.Combine(game, "MelonLoader", "Latest.log"),
            "[21:17:21.897] [Cuphead_Trainer] System.MissingMethodException: Method not found: 'System.Array.Empty'.\n" +
            "  at MelonLoader.MelonEvent+<>c.<Invoke>b__1_0 (MelonLoader.LemonAction x) [0x00000] in <filename unknown>:0");
        var main = new MainWindowViewModel();
        main.GameDiscovery.SelectedGame = new GameDiscoveryService().InspectGame(game);
        await main.GameDiscovery.LoadLogCommand.ExecuteAsync(null);
        var diagnosis = main.LogAnalyzer.Diagnosis!;
        Assert.False(diagnosis.IsBclTransplantCandidate, $"Category={diagnosis.Category}, Summary={diagnosis.Summary}");
    }

    [Fact]
    public void BepInEx5FatalLineIsLoaderBootstrap()
    {
        // BepInEx 5 prefixes lines as "[Level  :   Source]".
        Assert.Equal(CrashCategory.LoaderBootstrap, analyzer.Analyze("[Fatal  :   BepInEx] Unable to run chainloader").Category);
    }

    [Fact]
    public void MissingNuGetPackageShippedByModIsNotTransplantCandidate()
    {
        var result = analyzer.Analyze("System.IO.FileNotFoundException: Could not load file or assembly 'System.Runtime.CompilerServices.Unsafe, Version=4.0.4.1, Culture=neutral, PublicKeyToken=b03f5f7f11d50a3a' or one of its dependencies.");
        Assert.False(result.IsBclTransplantCandidate, $"Category={result.Category}");
    }

    [Fact]
    public void Il2cppEngineIcallIsNotLabelledGameCodeByNameGuess()
    {
        // Real line: "player" in VideoPlayer made the heuristic call Unity engine code "game code".
        var result = analyzer.Analyze("System.MissingMethodException: VideoPlayer::set_url not found in the il2cpp runtime.");
        Assert.NotEqual(CrashCategory.GameCodeStripping, result.Category);
        Assert.False(result.IsBclTransplantCandidate);
    }

    // ---------- Transplant / restore ----------

    [Fact]
    public void ReferenceAssemblyDonorIsRejected()
    {
        var game = Path.Combine(root, "Managed");
        var donor = Path.Combine(root, "4.7.1-api");
        Dll(game, "System.Core.dll");
        Dll(donor, "System.Core.dll", referenceAssembly: true);
        var original = File.ReadAllBytes(Path.Combine(game, "System.Core.dll"));
        var result = new BclTransplantService().ExecuteTransplant(donor, game, new[] { "System.Core.dll" }, true);
        Assert.False(result.Success, "a metadata-only reference assembly was copied into the game");
        Assert.Equal(original, File.ReadAllBytes(Path.Combine(game, "System.Core.dll")));
    }

    [Fact]
    public void RestoreDoesNotRevertFilesChangedAfterTransplant()
    {
        var game = Path.Combine(root, "Managed");
        var donor = Path.Combine(root, "Donor");
        Dll(game, "System.Core.dll");
        Dll(game, "Assembly-CSharp.dll", version: 1);
        Dll(donor, "System.Core.dll", version: 5);
        var originalCore = File.ReadAllBytes(Path.Combine(game, "System.Core.dll"));
        var service = new BclTransplantService();
        var result = service.ExecuteTransplant(donor, game, new[] { "System.Core.dll" }, true);
        Assert.True(result.Success, result.LogSummary);
        Dll(game, "Assembly-CSharp.dll", version: 2); // e.g. a Steam update after the transplant
        var updated = File.ReadAllBytes(Path.Combine(game, "Assembly-CSharp.dll"));
        var restored = service.RestoreBackup(result.BackupPath, game, out var error);
        Assert.Equal(updated, File.ReadAllBytes(Path.Combine(game, "Assembly-CSharp.dll")));
        if (restored) Assert.Equal(originalCore, File.ReadAllBytes(Path.Combine(game, "System.Core.dll")));
        else Assert.NotEmpty(error);
    }

    [Fact]
    public void RestoreAfterAddOnlyTransplantDoesNotRevertFilesChangedLater()
    {
        var game = Path.Combine(root, "ManagedAddOnly");
        var donor = Path.Combine(root, "DonorAddOnly");
        Dll(game, "Assembly-CSharp.dll", version: 1);
        Dll(donor, "System.Data.dll", version: 4);
        var service = new BclTransplantService();
        var result = service.ExecuteTransplant(donor, game, new[] { "System.Data.dll" }, true);
        Assert.True(result.Success, result.LogSummary);
        Assert.True(File.Exists(Path.Combine(game, "System.Data.dll")));

        // Simulate a Steam update modifying game code after the add-only transplant.
        Dll(game, "Assembly-CSharp.dll", version: 2);
        var updated = File.ReadAllBytes(Path.Combine(game, "Assembly-CSharp.dll"));

        Assert.True(service.RestoreBackup(result.BackupPath, game, out var error), error);
        Assert.False(File.Exists(Path.Combine(game, "System.Data.dll")));
        Assert.Equal(updated, File.ReadAllBytes(Path.Combine(game, "Assembly-CSharp.dll")));
    }

    [Fact]
    public void RollbackAfterUnbackedSecondTransplantDoesNotLeaveMixedState()
    {
        var game = Path.Combine(root, "Managed");
        var donor = Path.Combine(root, "Donor");
        Dll(game, "System.Core.dll");
        Dll(donor, "System.Core.dll", version: 5);
        Dll(donor, "System.Data.dll");
        var originalCore = File.ReadAllBytes(Path.Combine(game, "System.Core.dll"));
        var diff = new AssemblyDiffViewModel { GameManagedPath = game, DonorBclPath = donor };
        var vm = new TransplantViewModel(diff);
        diff.ScanCommand.Execute(null);
        diff.AllDiffItems.Single(x => x.FileName == "System.Core.dll").IsSelectedForTransplant = true;
        vm.ExecuteTransplantCommand.Execute(null);
        vm.AutoBackup = false;
        diff.AllDiffItems.Single(x => x.FileName == "System.Data.dll").IsSelectedForTransplant = true;
        vm.ExecuteTransplantCommand.Execute(null);
        vm.RestoreLastBackupCommand.Execute(null);
        // Either everything is rolled back, or the rollback is refused and nothing moves.
        var coreRestored = File.ReadAllBytes(Path.Combine(game, "System.Core.dll")).SequenceEqual(originalCore);
        var dataPresent = File.Exists(Path.Combine(game, "System.Data.dll"));
        Assert.True(coreRestored != dataPresent, $"mixed state: System.Core restored={coreRestored}, System.Data.dll present={dataPresent}");
    }

    // ---------- Discovery ----------

    [Fact]
    public void JunctionedGameFolderIsNotSilentlyDropped()
    {
        if (!OperatingSystem.IsWindows()) return;
        var real = Path.Combine(root, "OtherDrive", "Moved Game");
        Directory.CreateDirectory(Path.Combine(real, "MovedGame_Data", "Managed"));
        var common = Path.Combine(root, "steamapps", "common");
        Directory.CreateDirectory(common);
        var link = Path.Combine(common, "Moved Game");
        using (var mklink = Process.Start(new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{real}\"") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true })!)
            mklink.WaitForExit();
        Assert.True(Directory.Exists(link), "junction was not created");
        var result = new GameDiscoveryService().Discover(new[] { root });
        Assert.True(result.Games.Count == 1 || result.Warnings.Count > 0,
            $"games={result.Games.Count}, warnings={result.Warnings.Count}: the junctioned game vanished without a warning");
    }

    [Fact]
    public void PlayerLogIsFoundInLocalLow()
    {
        // Unity writes Player.log to %USERPROFILE%\AppData\LocalLow\<company>\<product>; app.info holds both names.
        var game = Path.Combine(root, "steamapps", "common", "Logless");
        var data = Path.Combine(game, "Logless_Data");
        Directory.CreateDirectory(Path.Combine(data, "Managed"));
        var company = "BclToolAppTest_" + Guid.NewGuid().ToString("N");
        File.WriteAllText(Path.Combine(data, "app.info"), company + "\nLogless");
        var localLow = Path.Combine(Path.GetDirectoryName(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData))!, "LocalLow");
        var logDirectory = Path.Combine(localLow, company, "Logless");
        Directory.CreateDirectory(logDirectory);
        try
        {
            var log = Path.Combine(logDirectory, "Player.log");
            File.WriteAllText(log, "SIGSEGV");
            Assert.Equal(log, new GameDiscoveryService().InspectGame(game)!.LogPath);
        }
        finally
        {
            Directory.Delete(Path.Combine(localLow, company), true);
        }
    }

    // ---------- UI ----------

    [Fact]
    public async Task SelectedGameSurvivesListRefreshInRealListBox()
    {
        foreach (var name in new[] { "Alpha", "Beta" }) // empty Managed -> MissingFiles -> shown by default
            Directory.CreateDirectory(Path.Combine(root, "steamapps", "common", name, name + "_Data", "Managed"));
        using var session = HeadlessUnitTestSession.StartNew(typeof(HeadlessTestApp));
        var (before, afterRecheck, afterSearch) = await session.Dispatch(async () =>
        {
            var vm = new MainWindowViewModel().GameDiscovery;
            vm.SearchRoot = root;
            await vm.DiscoverCommand.ExecuteAsync(null);
            var list = new ListBox { DataContext = vm };
            list.Bind(ItemsControl.ItemsSourceProperty, new Binding(nameof(vm.FilteredGames)));
            list.Bind(SelectingItemsControl.SelectedItemProperty, new Binding(nameof(vm.SelectedGame)) { Mode = BindingMode.TwoWay });
            var window = new Window { Content = list };
            window.Show();
            Dispatcher.UIThread.RunJobs();
            list.SelectedIndex = 0;
            Dispatcher.UIThread.RunJobs();
            var selected = vm.SelectedGame?.Name;
            await vm.RecheckSelectedCommand.ExecuteAsync(null); // replaces FilteredGames
            Dispatcher.UIThread.RunJobs();
            var rechecked = vm.SelectedGame?.Name;
            vm.SearchQuery = "a"; // both names still match; replaces FilteredGames again
            Dispatcher.UIThread.RunJobs();
            var searched = vm.SelectedGame?.Name;
            window.Close();
            return (selected, rechecked, searched);
        }, CancellationToken.None);
        Assert.Equal("Alpha", before);
        Assert.Equal("Alpha", afterRecheck);
        Assert.Equal("Alpha", afterSearch);
    }

    [Fact]
    public void LeftoverDataDirectoryWithoutExecutableOrManagedIsIgnored()
    {
        var leftover = Path.Combine(root, "steamapps", "common", "UninstalledDemo");
        Directory.CreateDirectory(Path.Combine(leftover, "UninstalledDemo_Data"));
        Assert.Null(new GameDiscoveryService().InspectGame(leftover));
    }

    [Fact]
    public void ReadLogTailHandlesLargeUtf8LogWithSeek()
    {
        var logPath = Path.Combine(root, "large.log");
        var padding = new string('가', 10_000) + "\n";
        var marker = "FINAL_CRASH_LINE_한글_END";
        File.WriteAllText(logPath, padding + padding + marker, System.Text.Encoding.UTF8);

        var tail = new GameDiscoveryService().ReadLogTail(logPath, 50);
        Assert.Equal(50, tail.Length);
        Assert.EndsWith(marker, tail);
    }

    public void Dispose()
    {
        try { Directory.Delete(root, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}
