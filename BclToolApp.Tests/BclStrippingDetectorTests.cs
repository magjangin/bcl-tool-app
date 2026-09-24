using BclToolApp.Models;
using BclToolApp.Services;
using BclToolApp.ViewModels;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace BclToolApp.Tests;

public sealed class BclStrippingDetectorTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "BclStrippingTests", Guid.NewGuid().ToString("N"));
    private readonly BclStrippingDetectorService detector = new();
    public BclStrippingDetectorTests() => Directory.CreateDirectory(root);

    // Construct valid managed metadata, then remove APIs as a linker would.
    private static void WriteFixture(string directory, Func<BclStrippingDetectorService.ApiProbe, bool>? keep = null, int mscorlibMajor = 4)
    {
        Directory.CreateDirectory(directory);
        foreach (var group in BclStrippingDetectorService.Probes.GroupBy(p => p.Assembly))
        {
            var version = new Version(group.Key == "mscorlib" ? mscorlibMajor : 4, 0, 0, 0);
            using var asm = AssemblyDefinition.CreateAssembly(new AssemblyNameDefinition("Fixture." + group.Key, version), group.Key, ModuleKind.Dll);
            var module = asm.MainModule;
            if (group.Key == "mscorlib")
            {
                var objectType = new TypeDefinition("System", "Object", TypeAttributes.Public);
                module.Types.Add(objectType);
                module.Types.Add(new TypeDefinition("System", "Void", TypeAttributes.Public | TypeAttributes.Sealed, objectType));
            }
            foreach (var types in group.GroupBy(p => p.Type))
            {
                var split = types.Key.LastIndexOf('.');
                var type = new TypeDefinition(types.Key[..split], types.Key[(split + 1)..], TypeAttributes.Public, module.TypeSystem.Object);
                module.Types.Add(type);
                if (type.Name.Contains('`'))
                    for (int i = 0; i < int.Parse(type.Name.Split('`')[1]); i++) type.GenericParameters.Add(new GenericParameter("T" + i, type));
                foreach (var probe in types.Where(p => keep?.Invoke(p) ?? true))
                {
                    var method = new MethodDefinition(probe.Method, MethodAttributes.Public | MethodAttributes.Static, module.TypeSystem.Void);
                    type.Methods.Add(method);
                    for (int i = 0; i < probe.GenericArity; i++) method.GenericParameters.Add(new GenericParameter("M" + i, method));
                    foreach (var signature in probe.Parameters) method.Parameters.Add(new ParameterDefinition(Parse(signature, module, type, method)));
                    method.Body.Instructions.Add(Instruction.Create(OpCodes.Ret));
                }
            }
            asm.Name.Name = group.Key;
            asm.Write(Path.Combine(directory, group.Key + ".dll"));
        }
    }

    private static TypeReference Parse(string signature, ModuleDefinition module, TypeDefinition owner, MethodDefinition method)
    {
        if (signature.EndsWith('&')) return new ByReferenceType(Parse(signature[..^1], module, owner, method));
        if (signature.StartsWith("!!")) return method.GenericParameters[int.Parse(signature[2..])];
        if (signature.StartsWith('!')) return owner.GenericParameters[int.Parse(signature[1..])];
        int generic = signature.IndexOf('<');
        if (generic >= 0)
        {
            var result = new GenericInstanceType(Parse(signature[..generic], module, owner, method));
            foreach (var argument in signature[(generic + 1)..^1].Split(',')) result.GenericArguments.Add(Parse(argument, module, owner, method));
            return result;
        }
        int split = signature.LastIndexOf('.');
        return new TypeReference(signature[..split], signature[(split + 1)..], module, module.TypeSystem.CoreLibrary);
    }

    [Fact]
    public void CompleteSamplesDoNotClaimEntireAssemblyIsUnstripped()
    {
        WriteFixture(root);
        var report = detector.InspectManagedDirectory(root);
        Assert.Equal(BclStrippingStatus.NoProbeGaps, report.Status);
        Assert.Equal(BclStrippingDetectorService.Probes.Count, report.CheckedApis);
        Assert.Equal(0, report.MissingApis);
        Assert.Contains("전체 무결성은 미확인", report.Summary);
    }

    [Fact]
    public void LoaderCrashPointsAreCaughtWhenCommonApisSurvive()
    {
        // Shape of Neon Abyss's original mscorlib: every common API kept, GetPEKind and most of TypeInfo stripped.
        WriteFixture(root, p => p.Method != "GetPEKind" && p.Type != "System.Reflection.TypeInfo");
        var report = detector.InspectManagedDirectory(root);
        Assert.Equal(BclStrippingStatus.Suspected, report.Status);
        Assert.Equal(3, report.MissingApis);
        Assert.Contains(report.Evidence, e => e.Contains("[메서드 누락]") && e.Contains("Module.GetPEKind"));
        Assert.Contains(report.Evidence, e => e.Contains("TypeInfo.GetDeclaredMethod"));
    }

    [Fact]
    public void ProbesForNewerProfileAreSkippedOnOldMscorlib()
    {
        // mscorlib 2.0 never had TypeInfo (.NET 4.5); that is the profile, not stripping.
        WriteFixture(root, p => p.Type != "System.Reflection.TypeInfo", mscorlibMajor: 2);
        var report = detector.InspectManagedDirectory(root);
        Assert.Equal(BclStrippingStatus.NoProbeGaps, report.Status);
        Assert.Equal(0, report.MissingApis);
        Assert.Equal(BclStrippingDetectorService.Probes.Count(p => p.MinMajorVersion <= 2), report.CheckedApis);
    }

    [Fact]
    public void RemovedMethodIsDetectedWithSpecificEvidence()
    {
        WriteFixture(root, p => p.Method != "GetILGenerator");
        var report = detector.InspectManagedDirectory(root);
        Assert.Equal(BclStrippingStatus.Suspected, report.Status);
        Assert.Equal(1, report.MissingApis);
        Assert.Contains(report.Evidence, e => e.Contains("[메서드 누락]") && e.Contains("DynamicMethod.GetILGenerator"));
    }

    [Fact]
    public void MissingOverloadIsNotMaskedByOtherOverloadWithSameName()
    {
        WriteFixture(root, p => p.Method != "Emit" || p.Parameters.Length == 1);
        var report = detector.InspectManagedDirectory(root);
        Assert.Equal(1, report.MissingApis);
        Assert.Contains(report.Evidence, e => e.Contains("ILGenerator.Emit") && e.Contains("MethodInfo"));
    }

    [Fact]
    public void RemovedTypeIsDetected()
    {
        WriteFixture(root);
        var path = Path.Combine(root, "mscorlib.dll");
        using (var asm = AssemblyDefinition.ReadAssembly(path, new ReaderParameters { InMemory = true }))
        {
            asm.MainModule.Types.Remove(asm.MainModule.Types.Single(t => t.FullName == "System.Reflection.Emit.DynamicMethod"));
            asm.Write(path);
        }
        var report = detector.InspectManagedDirectory(root);
        Assert.Contains(report.Evidence, e => e.Contains("[타입 누락]") && e.Contains("DynamicMethod"));
    }

    [Fact]
    public void MissingFileIsDifferentFromStrippedApis()
    {
        WriteFixture(root);
        File.Delete(Path.Combine(root, "System.Core.dll"));
        var report = detector.InspectManagedDirectory(root);
        Assert.Equal(BclStrippingStatus.MissingFiles, report.Status);
        Assert.Equal(0, report.MissingApis);
        Assert.True(report.NeedsReview);
    }

    [Fact]
    public void CorruptFileIsInconclusiveNotStripped()
    {
        WriteFixture(root);
        File.WriteAllText(Path.Combine(root, "mscorlib.dll"), "corrupt");
        Assert.Equal(BclStrippingStatus.Inconclusive, detector.InspectManagedDirectory(root).Status);
    }

    [Fact]
    public void UnsupportedVersionIsNotAssumedStripped()
    {
        WriteFixture(root);
        var path = Path.Combine(root, "mscorlib.dll");
        using (var asm = AssemblyDefinition.ReadAssembly(path, new ReaderParameters { InMemory = true }))
        {
            asm.Name.Version = new Version(9, 0);
            asm.Write(path);
        }
        Assert.Equal(BclStrippingStatus.Inconclusive, detector.InspectManagedDirectory(root).Status);
    }

    [Fact]
    public void ForwardedTypeIsNotReportedAsMissing()
    {
        WriteFixture(root);
        var path = Path.Combine(root, "mscorlib.dll");
        using (var asm = AssemblyDefinition.ReadAssembly(path, new ReaderParameters { InMemory = true }))
        {
            asm.MainModule.Types.Remove(asm.MainModule.Types.Single(t => t.FullName == "System.Reflection.Emit.DynamicMethod"));
            var reference = new AssemblyNameReference("Other", new Version(4, 0));
            asm.MainModule.AssemblyReferences.Add(reference);
            asm.MainModule.ExportedTypes.Add(new ExportedType("System.Reflection.Emit", "DynamicMethod", asm.MainModule, reference) { IsForwarder = true });
            asm.Write(path);
        }
        var report = detector.InspectManagedDirectory(root);
        Assert.Equal(BclStrippingStatus.Inconclusive, report.Status);
        Assert.Equal(0, report.MissingApis);
    }

    [Fact]
    public void RecheckUsesCurrentBytesAndDoesNotInferHistoryFromGameName()
    {
        var path = Path.Combine(root, "Neon Abyss");
        WriteFixture(path, p => p.Method != "Select");
        Assert.Equal(BclStrippingStatus.Suspected, detector.InspectManagedDirectory(path).Status);
        WriteFixture(path);
        Assert.Equal(BclStrippingStatus.NoProbeGaps, detector.InspectManagedDirectory(path).Status);
    }

    [Fact]
    public void Il2cppIsNotReportedAsMissingBcl()
    {
        var report = detector.Inspect(new InstalledGame { Runtime = "IL2CPP", ManagedPath = root });
        Assert.Equal(BclStrippingStatus.NotSupported, report.Status);
        Assert.False(report.NeedsReview);
    }

    [Fact]
    public async Task DefaultListShowsSuspectsAndFilterCanRevealPassedGames()
    {
        var complete = Path.Combine(root, "steamapps", "common", "Complete", "Complete_Data", "Managed");
        var stripped = Path.Combine(root, "steamapps", "common", "Stripped", "Stripped_Data", "Managed");
        WriteFixture(complete);
        WriteFixture(stripped, p => p.Method != "Select");
        var vm = new MainWindowViewModel().GameDiscovery;
        vm.SearchRoot = root;
        await vm.DiscoverCommand.ExecuteAsync(null);
        Assert.Equal("Stripped", Assert.Single(vm.FilteredGames).Name);
        vm.OnlySuspected = false;
        Assert.Equal(2, vm.FilteredGames.Count);
        Assert.Equal("Stripped", vm.FilteredGames[0].Name);
        vm.SelectedGame = vm.FilteredGames[0];
        WriteFixture(stripped);
        await vm.RecheckSelectedCommand.ExecuteAsync(null);
        Assert.Equal(BclStrippingStatus.NoProbeGaps, vm.SelectedGame!.Stripping.Status);
    }

    [Fact]
    public void CancellationStopsDiscovery()
    {
        Directory.CreateDirectory(Path.Combine(root, "steamapps", "common", "A", "A_Data"));
        Assert.Throws<OperationCanceledException>(() => new GameDiscoveryService().Discover(new[] { root }, new CancellationToken(true)));
    }

    public void Dispose() => Directory.Delete(root, true);
}


