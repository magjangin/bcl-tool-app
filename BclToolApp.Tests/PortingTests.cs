using BclToolApp.Services;
using BclToolApp.Models;
using BclToolApp.ViewModels;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace BclToolApp.Tests;

public sealed class PortingTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "BclToolTests", Guid.NewGuid().ToString("N"));
    private string Game => Path.Combine(root, "Managed");
    private string Donor => Path.Combine(root, "Donor");
    private readonly BclTransplantService transplant = new();
    private readonly BclAssemblyInspectorService inspector = new();

    public PortingTests()
    {
        Directory.CreateDirectory(Game);
        Directory.CreateDirectory(Donor);
    }

    private static void Assembly(string folder, string name = "System.Core.dll", int version = 1, string method = "Select")
    {
        using var asm = AssemblyDefinition.CreateAssembly(new AssemblyNameDefinition(Path.GetFileNameWithoutExtension(name), new Version(version, 0)), name, ModuleKind.Dll);
        var type = new TypeDefinition("Example", "Outer", TypeAttributes.Public | TypeAttributes.Class, asm.MainModule.TypeSystem.Object);
        asm.MainModule.Types.Add(type);
        var nested = new TypeDefinition("", "Inner", TypeAttributes.NestedPublic | TypeAttributes.Class, asm.MainModule.TypeSystem.Object);
        type.NestedTypes.Add(nested);
        var member = new MethodDefinition(method, MethodAttributes.Public | MethodAttributes.Static, asm.MainModule.TypeSystem.Void);
        member.Body.Instructions.Add(Instruction.Create(OpCodes.Ret));
        nested.Methods.Add(member);
        asm.Write(Path.Combine(folder, name));
    }

    [Fact]
    public void RestoreRevertsReplacementAndRemovesOnlyTransplantedAdditions()
    {
        Assembly(Game);
        var original = File.ReadAllBytes(Path.Combine(Game, "System.Core.dll"));
        Assembly(Donor, version: 2);
        Assembly(Donor, "System.Xml.dll");
        Directory.CreateDirectory(Path.Combine(Game, "nested"));
        File.WriteAllText(Path.Combine(Game, "nested", "keep.txt"), "keep");
        var result = transplant.ExecuteTransplant(Donor, Game, new[] { "System.Core.dll", "System.Xml.dll" }, true);
        Assert.True(result.Success, result.LogSummary);
        Assert.Equal(2, result.CopiedFiles.Count);
        File.WriteAllText(Path.Combine(Game, "unrelated.txt"), "later");
        Assert.True(transplant.RestoreBackup(result.BackupPath, Game, out var error), error);
        Assert.Equal(original, File.ReadAllBytes(Path.Combine(Game, "System.Core.dll")));
        Assert.False(File.Exists(Path.Combine(Game, "System.Xml.dll")));
        Assert.Equal("later", File.ReadAllText(Path.Combine(Game, "unrelated.txt")));
        Assert.Equal("keep", File.ReadAllText(Path.Combine(Game, "nested", "keep.txt")));
    }

    [Fact]
    public void BackupsAreUniqueAndPreserveFirstSnapshot()
    {
        File.WriteAllText(Path.Combine(Game, "config.txt"), "original");
        var first = transplant.CreateBackup(Game);
        File.WriteAllText(Path.Combine(Game, "config.txt"), "changed");
        var second = transplant.CreateBackup(Game);
        Assert.NotEqual(first, second);
        Assert.Equal("original", File.ReadAllText(Path.Combine(first, "config.txt")));
    }

    [Theory]
    [InlineData("../escape.dll")]
    [InlineData("..\\escape.dll")]
    [InlineData("C:\\escape.dll")]
    [InlineData("bad.txt")]
    [InlineData("missing.dll")]
    [InlineData("System.Core.dll:stream")]
    public void InvalidBatchDoesNotChangeAnyFile(string invalid)
    {
        Assembly(Game);
        Assembly(Donor, version: 2);
        var original = File.ReadAllBytes(Path.Combine(Game, "System.Core.dll"));
        var result = transplant.ExecuteTransplant(Donor, Game, new[] { "System.Core.dll", invalid }, true);
        Assert.False(result.Success);
        Assert.Empty(result.CopiedFiles);
        Assert.Empty(result.BackupPath);
        Assert.NotEmpty(result.LogSummary);
        Assert.Equal(original, File.ReadAllBytes(Path.Combine(Game, "System.Core.dll")));
    }

    [Fact]
    public void RejectsEmptySameDirectoryAndNonManagedInputs()
    {
        Assert.False(transplant.ExecuteTransplant(Donor, Game, Array.Empty<string>(), false).Success);
        Assert.False(transplant.ExecuteTransplant(Game, Game, new[] { "a.dll" }, false).Success);
        File.WriteAllText(Path.Combine(Donor, "native.dll"), "not a managed DLL");
        Assert.False(transplant.ExecuteTransplant(Donor, Game, new[] { "native.dll" }, false).Success);
        Assert.False(File.Exists(Path.Combine(Game, "native.dll")));
    }

    [Fact]
    public void CopyFailureRollsBackPreviouslyCopiedFilesWithoutPersistentBackup()
    {
        Assembly(Game);
        Assembly(Donor, version: 2);
        Assembly(Donor, "System.Xml.dll");
        Directory.CreateDirectory(Path.Combine(Game, "System.Xml.dll"));
        var original = File.ReadAllBytes(Path.Combine(Game, "System.Core.dll"));
        var result = transplant.ExecuteTransplant(Donor, Game, new[] { "System.Core.dll", "System.Xml.dll" }, false);
        Assert.False(result.Success);
        Assert.Equal(original, File.ReadAllBytes(Path.Combine(Game, "System.Core.dll")));
    }

    [Fact]
    public void RestoreRejectsWrongGameAndCorruptedBackupBeforeWriting()
    {
        Assembly(Game);
        var backup = transplant.CreateBackup(Game);
        Assert.False(transplant.RestoreBackup(backup, Donor, out _));
        Assembly(Game, version: 2);
        var current = File.ReadAllBytes(Path.Combine(Game, "System.Core.dll"));
        File.WriteAllText(Path.Combine(backup, "System.Core.dll"), "corrupt");
        Assert.False(transplant.RestoreBackup(backup, Game, out _));
        Assert.Equal(current, File.ReadAllBytes(Path.Combine(Game, "System.Core.dll")));
    }

    [Fact]
    public void SameVersionDoesNotMeanSameContent()
    {
        Assembly(Game);
        Assembly(Donor, method: "SelectMany");
        Assert.Equal(AssemblyDiffStatus.ContentMismatch, Assert.Single(inspector.CompareDirectories(Game, Donor)).Status);
        File.Copy(Path.Combine(Game, "System.Core.dll"), Path.Combine(Donor, "System.Core.dll"), true);
        Assert.Equal(AssemblyDiffStatus.Identical, Assert.Single(inspector.CompareDirectories(Game, Donor)).Status);
    }

    [Fact]
    public void InvalidAssembliesAreNotIdenticalOrSelectable()
    {
        File.WriteAllText(Path.Combine(Game, "System.Core.dll"), "invalid");
        File.WriteAllText(Path.Combine(Donor, "System.Core.dll"), "invalid");
        var item = Assert.Single(inspector.CompareDirectories(Game, Donor));
        Assert.Equal(AssemblyDiffStatus.InvalidAssembly, item.Status);
        Assert.False(item.CanTransplant);
    }

    [Fact]
    public void ComparisonReportsMissingExtraAndVersionMismatch()
    {
        Assembly(Game, "Extra.dll");
        Assembly(Donor, "Missing.dll");
        Assembly(Game);
        Assembly(Donor, version: 2);
        var items = inspector.CompareDirectories(Game, Donor);
        Assert.Contains(items, x => x.Status == AssemblyDiffStatus.ExtraInGame && !x.CanTransplant);
        Assert.Contains(items, x => x.Status == AssemblyDiffStatus.MissingInGame && !x.IsSelectedForTransplant);
        Assert.Contains(items, x => x.Status == AssemblyDiffStatus.VersionMismatch);
        Assert.Throws<DirectoryNotFoundException>(() => inspector.CompareDirectories(Game, Path.Combine(root, "absent")));
    }

    [Theory]
    [InlineData("Example.Outer+Inner", true)]
    [InlineData("Example.Outer/Inner.Select", true)]
    [InlineData("Select", true)]
    [InlineData("Sele", false)]
    [InlineData("", false)]
    [InlineData("   ", false)]
    public void SymbolSearchSupportsNestedTypesWithoutSubstringFalsePositives(string query, bool expected)
    {
        Assembly(Game);
        Assert.Equal(expected, inspector.CheckMemberExistsInAssembly(Path.Combine(Game, "System.Core.dll"), query).Found);
    }

    [Fact]
    public void SelectionNotifiesAndChangingPathsInvalidatesScan()
    {
        Assembly(Donor);
        var vm = new AssemblyDiffViewModel { GameManagedPath = Game, DonorBclPath = Donor };
        vm.ScanCommand.Execute(null);
        var item = Assert.Single(vm.AllDiffItems);
        var notified = false;
        item.PropertyChanged += (_, e) => notified |= e.PropertyName == nameof(item.IsSelectedForTransplant);
        vm.ToggleSelectAllMissingCommand.Execute(null);
        Assert.True(notified);
        Assert.True(item.IsSelectedForTransplant);
        vm.SelectedItem = item;
        vm.GameManagedPath = Path.Combine(root, "different");
        Assert.Empty(vm.AllDiffItems);
        Assert.Empty(vm.FilteredDiffItems);
        Assert.Null(vm.SelectedItem);
        vm.ScanCommand.Execute(null);
        Assert.Contains("존재", vm.StatusSummary);
    }

    [Fact]
    public void ViewModelRestoresOriginalGameAfterPathChanges()
    {
        Assembly(Game);
        Assembly(Donor, version: 2);
        var original = File.ReadAllBytes(Path.Combine(Game, "System.Core.dll"));
        var diff = new AssemblyDiffViewModel { GameManagedPath = Game, DonorBclPath = Donor };
        diff.ScanCommand.Execute(null);
        Assert.Single(diff.AllDiffItems).IsSelectedForTransplant = true;
        var vm = new TransplantViewModel(diff);
        vm.ExecuteTransplantCommand.Execute(null);
        Assert.True(vm.HasBackup);
        diff.GameManagedPath = Donor;
        vm.RestoreLastBackupCommand.Execute(null);
        Assert.Equal(original, File.ReadAllBytes(Path.Combine(Game, "System.Core.dll")));
        Assert.Contains("복원 완료", vm.ExecutionLog);
    }

    [Fact]
    public void DuplicateTargetsAreCopiedOnce()
    {
        Assembly(Donor);
        var result = transplant.ExecuteTransplant(Donor, Game, new[] { "System.Core.dll", "System.Core.dll" }, true);
        Assert.True(result.Success);
        Assert.Single(result.CopiedFiles);
    }

    [Fact]
    public void RestoreFailureRevertsEarlierRestoreWrites()
    {
        File.WriteAllText(Path.Combine(Game, "a.txt"), "original-a");
        File.WriteAllText(Path.Combine(Game, "b.txt"), "original-b");
        var backup = transplant.CreateBackup(Game);
        File.WriteAllText(Path.Combine(Game, "a.txt"), "current-a");
        File.WriteAllText(Path.Combine(Game, "b.txt"), "current-b");
        using var locked = new FileStream(Path.Combine(Game, "b.txt"), FileMode.Open, FileAccess.Read, FileShare.Read);
        Assert.False(transplant.RestoreBackup(backup, Game, out var error));
        Assert.NotEmpty(error);
        Assert.Equal("current-a", File.ReadAllText(Path.Combine(Game, "a.txt")));
    }

    public void Dispose() => Directory.Delete(root, true);
}

