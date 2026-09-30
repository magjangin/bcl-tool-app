using System.Text;
using BclToolApp.Models;
using BclToolApp.Services;
using BclToolApp.ViewModels;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace BclToolApp.Tests;

public sealed class DonorSearchTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "BclDonorTests", Guid.NewGuid().ToString("N"));
    private readonly DonorSearchService search = new();
    public DonorSearchTests() => Directory.CreateDirectory(root);

    /// <summary>Builds a Unity game folder: the version Unity stamps into globalgamemanagers plus one mscorlib.</summary>
    private InstalledGame CreateGame(string name, string unityVersion, int mscorlibMajor = 4,
        bool getPeKind = true, int methods = 100, bool referenceAssembly = false)
    {
        var install = Path.Combine(root, "steamapps", "common", name);
        var data = Path.Combine(install, name.Replace(" ", "") + "_Data");
        var managed = Path.Combine(data, "Managed");
        Directory.CreateDirectory(managed);
        if (unityVersion.Length > 0)
            File.WriteAllBytes(Path.Combine(data, "globalgamemanagers"),
                Encoding.ASCII.GetBytes("\0\0\0\u0008UnityFS\0" + unityVersion + "\0\0"));
        WriteMscorlib(managed, mscorlibMajor, getPeKind, methods, referenceAssembly);
        return new InstalledGame { Name = name, InstallPath = install, ManagedPath = managed, Runtime = "Mono" };
    }

    private static void WriteMscorlib(string managed, int major, bool getPeKind, int methods, bool referenceAssembly)
    {
        Directory.CreateDirectory(managed);
        using var asm = AssemblyDefinition.CreateAssembly(
            new AssemblyNameDefinition("Fixture.mscorlib", new Version(major, 0, 0, 0)), "mscorlib", ModuleKind.Dll);
        var module = asm.MainModule;
        var objectType = new TypeDefinition("System", "Object", TypeAttributes.Public);
        module.Types.Add(objectType);
        module.Types.Add(new TypeDefinition("System", "Void", TypeAttributes.Public | TypeAttributes.Sealed, objectType));
        var moduleType = new TypeDefinition("System.Reflection", "Module", TypeAttributes.Public, objectType);
        module.Types.Add(moduleType);
        if (getPeKind)
            moduleType.Methods.Add(new MethodDefinition("GetPEKind", MethodAttributes.Public, module.TypeSystem.Void));
        for (int i = 0; i < methods; i++)
            moduleType.Methods.Add(new MethodDefinition("Filler" + i, MethodAttributes.Public, module.TypeSystem.Void));
        if (referenceAssembly)
        {
            var attribute = new TypeReference("System.Runtime.CompilerServices", "ReferenceAssemblyAttribute", module, module.TypeSystem.CoreLibrary);
            asm.CustomAttributes.Add(new CustomAttribute(new MethodReference(".ctor", module.TypeSystem.Void, attribute) { HasThis = true }));
        }
        asm.Name.Name = "mscorlib";
        asm.Write(Path.Combine(managed, "mscorlib.dll"));
    }

    /// <summary>Writes one extra assembly whose size grows with the method count.</summary>
    private static void WriteAssembly(string directory, string name, int methods)
    {
        Directory.CreateDirectory(directory);
        using var asm = AssemblyDefinition.CreateAssembly(
            new AssemblyNameDefinition(Path.GetFileNameWithoutExtension(name), new Version(4, 0, 0, 0)), name, ModuleKind.Dll);
        var type = new TypeDefinition("Example", "Holder", TypeAttributes.Public, asm.MainModule.TypeSystem.Object);
        asm.MainModule.Types.Add(type);
        for (int i = 0; i < methods; i++)
        {
            var method = new MethodDefinition("M" + i, MethodAttributes.Public, asm.MainModule.TypeSystem.Void);
            method.Body.Instructions.Add(Instruction.Create(OpCodes.Ret));
            type.Methods.Add(method);
        }
        asm.Write(Path.Combine(directory, name));
    }

    private DonorSearchService.SearchResult Find(InstalledGame target, params InstalledGame[] donors) =>
        search.Find(target.ManagedPath, donors, includeUnityEditors: false);

    [Fact]
    public void ReadsUnityVersionStampedInGameData()
    {
        var game = CreateGame("Zombie Rollerz", "2019.4.27f1");
        Assert.Equal("2019.4.27f1", DonorSearchService.ReadUnityVersion(game.InstallPath));
        Assert.Equal((2019, 4, 27), DonorSearchService.Parse("2019.4.27f1"));
    }

    [Fact]
    public void ClosestPatchInSameLtsLineWins()
    {
        var target = CreateGame("Target", "2019.4.27f1", methods: 10);
        var near = CreateGame("Near", "2019.4.26f1", methods: 900);
        var far = CreateGame("Far", "2019.4.3f1", methods: 900);
        var result = Find(target, far, near);
        Assert.Equal("Near", result.AutoApply?.Name);
        Assert.Equal(near.ManagedPath, result.AutoApply?.BclPath);
        Assert.Equal(new[] { "Near", "Far" }, result.Candidates.Select(c => c.Name));
        Assert.All(result.Candidates, c => Assert.True(c.IsSafeLine));
    }

    // docs/02 §4-2: Mask of Mists shares Neon Abyss's engine exactly, but its mscorlib has 926 fewer methods.
    [Fact]
    public void FullerMscorlibBeatsTheNearerButThinnerDonor()
    {
        var target = CreateGame("Target", "2018.4.21f1", methods: 10);
        var thinSameVersion = CreateGame("MaskOfMists", "2018.4.21f1", methods: 25_180);
        var fullFartherPatch = CreateGame("OneStepFromEden", "2018.4.15f1", methods: 26_114);
        var result = Find(target, thinSameVersion, fullFartherPatch);
        Assert.Equal("OneStepFromEden", result.AutoApply?.Name);
        Assert.Equal("MaskOfMists", result.Candidates[1].Name);
        Assert.Contains(result.Candidates[1].Warnings, w => w.Contains("가장 온전하지 않습니다"));
        Assert.False(result.Candidates[1].IsSafeLine);
    }

    [Fact]
    public void MoreCompleteMscorlibWinsAtEqualDistance()
    {
        var target = CreateGame("Target", "2019.4.20f1", methods: 10);
        var thin = CreateGame("Thin", "2019.4.19f1", methods: 300);
        var full = CreateGame("Full", "2019.4.21f1", methods: 900);
        var result = Find(target, thin, full);
        Assert.Equal("Full", result.AutoApply?.Name);
        Assert.Equal(900 + 1, result.Candidates[0].Profile.MscorlibMethods);
    }

    [Fact]
    public void DonorThatIsItselfStrippedIsRejected()
    {
        var target = CreateGame("Target", "2019.4.27f1", methods: 10);
        var stripped = CreateGame("Stripped", "2019.4.26f1", getPeKind: false, methods: 900);
        var intact = CreateGame("Intact", "2019.4.20f1", methods: 200);
        var result = Find(target, stripped, intact);
        Assert.Equal("Intact", result.AutoApply?.Name);
        Assert.DoesNotContain(result.Candidates, c => c.Name == "Stripped");
        Assert.Contains(result.Notes, n => n.Contains("GetPEKind"));
    }

    [Fact]
    public void DifferentMonoProfileIsRejected()
    {
        var target = CreateGame("Target", "2019.4.27f1", mscorlibMajor: 4, methods: 10);
        var old = CreateGame("Old", "2019.4.26f1", mscorlibMajor: 2, methods: 900);
        var result = Find(target, old);
        Assert.Empty(result.Candidates);
        Assert.Contains(result.Notes, n => n.Contains("프로파일"));
    }

    [Fact]
    public void ReferenceAssemblyIsNeverOffered()
    {
        var target = CreateGame("Target", "2019.4.27f1", methods: 10);
        var reference = CreateGame("Reference", "2019.4.27f1", methods: 900, referenceAssembly: true);
        var result = Find(target, reference);
        Assert.Empty(result.Candidates);
        Assert.Contains(result.Notes, n => n.Contains("참조 전용"));
    }

    [Fact]
    public void OtherLtsLineIsShownButNeverAppliedAutomatically()
    {
        var target = CreateGame("Target", "2019.4.27f1", methods: 10);
        var otherLine = CreateGame("Other", "2018.4.15f1", methods: 900);
        var result = Find(target, otherLine);
        Assert.Equal("Other", result.Best?.Name);
        Assert.Null(result.AutoApply);
        Assert.False(result.Best!.IsSafeLine);
        Assert.Contains("다른 LTS 줄", result.Best.Reason);
    }

    [Fact]
    public void UnknownUnityVersionIsNotTreatedAsMatch()
    {
        var target = CreateGame("Target", "2019.4.27f1", methods: 10);
        var unknown = CreateGame("Unknown", string.Empty, methods: 900);
        var result = Find(target, unknown);
        Assert.Null(result.AutoApply);
        Assert.Contains("Unity 버전", result.Best!.Reason);
    }

    [Fact]
    public void TargetIsNotOfferedAsItsOwnDonor()
    {
        var target = CreateGame("Target", "2019.4.27f1", methods: 900);
        var result = Find(target, target);
        Assert.Empty(result.Candidates);
    }

    [Fact]
    public void SameLineCandidateHidesRiskierTiers()
    {
        var target = CreateGame("Target", "2019.4.27f1", methods: 10);
        var same = CreateGame("Same", "2019.4.26f1", methods: 200);
        var other = CreateGame("Other", "2018.4.15f1", methods: 900);
        var unknown = CreateGame("Unknown", string.Empty, methods: 900);
        var result = Find(target, other, unknown, same);
        Assert.Equal("Same", Assert.Single(result.Candidates).Name);
    }

    [Fact]
    public void WeakerDonorIsFlaggedEvenWhenItIsTheOnlyOne()
    {
        var target = CreateGame("Target", "2019.4.27f1", methods: 900);
        var weak = CreateGame("Weak", "2019.4.26f1", methods: 100);
        var result = Find(target, weak);
        Assert.Equal("Weak", result.AutoApply?.Name);
        Assert.Contains(result.Best!.Warnings, w => w.Contains("많지 않습니다"));
    }

    [Fact]
    public async Task SelectingGameInDetectionTabFillsTheDonorPath()
    {
        CreateGame("Target", "2019.4.27f1", methods: 10);
        CreateGame("Donor", "2019.4.26f1", methods: 900);
        var vm = new MainWindowViewModel();
        vm.AssemblyDiff.SearchUnityEditors = false;
        vm.GameDiscovery.SearchRoot = root;
        await vm.GameDiscovery.DiscoverCommand.ExecuteAsync(null);
        vm.GameDiscovery.OnlySuspected = false;
        vm.GameDiscovery.SelectedGame = vm.GameDiscovery.FilteredGames.Single(g => g.Name == "Target");
        await vm.GameDiscovery.UseAsTargetCommand.ExecuteAsync(null);

        Assert.Equal(2, vm.SelectedTabIndex);
        Assert.Equal(Path.Combine(root, "steamapps", "common", "Donor", "Donor_Data", "Managed"), vm.AssemblyDiff.DonorBclPath);
        Assert.Equal("Donor", vm.AssemblyDiff.SelectedDonorCandidate?.Name);
        Assert.True(vm.AssemblyDiff.HasDonorCandidates);
        Assert.Contains("도너 자동 지정", vm.AssemblyDiff.DonorSearchStatus);
    }

    [Fact]
    public async Task ChangingTheTargetDropsCandidatesRankedForThePreviousGame()
    {
        CreateGame("Target", "2019.4.27f1", methods: 10);
        CreateGame("Donor", "2019.4.26f1", methods: 900);
        var vm = new MainWindowViewModel();
        vm.AssemblyDiff.SearchUnityEditors = false;
        vm.GameDiscovery.SearchRoot = root;
        await vm.GameDiscovery.DiscoverCommand.ExecuteAsync(null);
        vm.GameDiscovery.OnlySuspected = false;
        vm.GameDiscovery.SelectedGame = vm.GameDiscovery.FilteredGames.Single(g => g.Name == "Target");
        await vm.GameDiscovery.UseAsTargetCommand.ExecuteAsync(null);

        vm.AssemblyDiff.GameManagedPath = Path.Combine(root, "steamapps", "common", "Donor", "Donor_Data", "Managed");
        Assert.False(vm.AssemblyDiff.HasDonorCandidates);
        Assert.Null(vm.AssemblyDiff.SelectedDonorCandidate);
    }

    [Theory]
    [InlineData("")]                       // the game folder, as pasted from Explorer
    [InlineData("_Data")]                  // the *_Data folder
    [InlineData("_Data\\Managed")]         // the Managed folder itself
    [InlineData("\\Target.exe")]           // the executable
    public void AnyPathInsideTheGameResolvesToItsManagedFolder(string suffix)
    {
        var game = CreateGame("Target", "2019.4.27f1");
        var install = game.InstallPath;
        File.WriteAllText(Path.Combine(install, "Target.exe"), "fixture");
        var input = suffix.Length == 0 ? install
            : suffix.StartsWith('\\') ? install + suffix
            : Path.Combine(install, "Target" + suffix);
        var (resolved, _) = GameDiscoveryService.ResolveBclDirectory("\"" + input + "\"");
        Assert.Equal(game.ManagedPath, resolved);
    }

    [Fact]
    public void Il2cppGameIsRejectedWithItsOwnReason()
    {
        var install = Path.Combine(root, "steamapps", "common", "Il2cppGame");
        Directory.CreateDirectory(Path.Combine(install, "Il2cppGame_Data", "il2cpp_data", "Metadata"));
        File.WriteAllText(Path.Combine(install, "GameAssembly.dll"), "native");
        var (resolved, note) = GameDiscoveryService.ResolveBclDirectory(install);
        Assert.Null(resolved);
        Assert.Contains("IL2CPP", note);
    }

    [Fact]
    public async Task OneButtonResolvesThePathFindsTheDonorAndChecksOnlyTheStrippedBcl()
    {
        var target = CreateGame("Target", "2019.4.27f1", methods: 10);
        var donor = CreateGame("Donor", "2019.4.26f1", methods: 900);
        // Stripped BCL in the game, intact in the donor, plus files that must never be transplanted.
        WriteAssembly(target.ManagedPath, "System.Core.dll", 5);
        WriteAssembly(donor.ManagedPath, "System.Core.dll", 400);
        WriteAssembly(target.ManagedPath, "Assembly-CSharp.dll", 5);
        WriteAssembly(donor.ManagedPath, "Assembly-CSharp.dll", 400);
        WriteAssembly(donor.ManagedPath, "ZLinq.dll", 400);
        WriteAssembly(donor.ManagedPath, "System.Memory.dll", 400);

        var vm = new MainWindowViewModel();
        vm.AssemblyDiff.SearchUnityEditors = false;
        vm.AssemblyDiff.KnownGames = () => new[] { target, donor };
        vm.AssemblyDiff.GameManagedPath = target.InstallPath;   // the game folder, not Managed
        await vm.AssemblyDiff.FindDonorCommand.ExecuteAsync(null);

        Assert.Equal(target.ManagedPath, vm.AssemblyDiff.GameManagedPath);
        Assert.Equal(donor.ManagedPath, vm.AssemblyDiff.DonorBclPath);
        Assert.Equal(new[] { "mscorlib.dll", "System.Core.dll" },
            vm.AssemblyDiff.AllDiffItems.Where(x => x.IsSelectedForTransplant).Select(x => x.FileName).OrderBy(x => x));
        Assert.Contains("이식 대상 2개", vm.AssemblyDiff.StatusSummary);

        var gameCode = vm.AssemblyDiff.AllDiffItems.Single(x => x.FileName == "Assembly-CSharp.dll");
        Assert.False(gameCode.CanTransplant);
        Assert.False(gameCode.IsRecommended);
        Assert.DoesNotContain(vm.AssemblyDiff.FilteredDiffItems, x => x.FileName is "ZLinq.dll" or "Assembly-CSharp.dll" or "System.Memory.dll");
    }

    [Fact]
    public async Task DonorSearchWithoutTargetDoesNotScanAnything()
    {
        var vm = new MainWindowViewModel();
        vm.AssemblyDiff.SearchUnityEditors = false;
        await vm.AssemblyDiff.FindDonorCommand.ExecuteAsync(null);
        Assert.False(vm.AssemblyDiff.HasDonorCandidates);
        Assert.Contains("경로를 입력하세요", vm.AssemblyDiff.DonorSearchStatus);
    }

    public void Dispose() => Directory.Delete(root, true);
}
