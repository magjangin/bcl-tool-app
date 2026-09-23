using BclToolApp.Services;
using BclToolApp.ViewModels;

namespace BclToolApp.Tests;

public sealed class GameDiscoveryTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "BclDiscoveryTests", Guid.NewGuid().ToString("N"));
    private readonly GameDiscoveryService service = new();
    public GameDiscoveryTests() => Directory.CreateDirectory(root);
    private string CreateGame(string library, string name, bool il2cpp = false)
    {
        var path = Path.Combine(library, "steamapps", "common", name);
        Directory.CreateDirectory(Path.Combine(path, name.Replace(" ", "") + "_Data", "Managed"));
        Directory.CreateDirectory(Path.Combine(path, "MelonLoader"));
        if (il2cpp) File.WriteAllText(Path.Combine(path, "GameAssembly.dll"), "fixture");
        return path;
    }

    [Fact]
    public void FindsGamesWithSpacesAndTheirManagedDirectories()
    {
        CreateGame(root, "Neon Abyss");
        CreateGame(root, "Zombie Rollerz");
        Directory.CreateDirectory(Path.Combine(root, "steamapps", "common", "NotUnity"));
        Directory.CreateDirectory(Path.Combine(root, "steamapps", "common", "OtherGame_Data"));
        var result = service.Discover(new[] { root });
        Assert.Equal(2, result.Games.Count);
        Assert.Empty(result.Warnings);
        Assert.All(result.Games, g => { Assert.True(g.CanUseManaged); Assert.True(Directory.Exists(g.ManagedPath)); Assert.Equal("MelonLoader", g.Loader); });
    }

    [Fact]
    public void LibraryFoldersAddsExternalLibraryAndDeduplicates()
    {
        var external = Path.Combine(root, "External");
        CreateGame(root, "First");
        CreateGame(external, "Second");
        File.WriteAllText(Path.Combine(root, "steamapps", "libraryfolders.vdf"), "\"libraryfolders\" { \"0\" { \"path\" \"" + external.Replace("\\", "\\\\") + "\" } }");
        var result = service.Discover(new[] { root, external });
        Assert.Equal(2, result.Games.Count);
        Assert.Empty(result.Warnings);
    }

    [Fact]
    public void AcceptsCommonSteamappsAndDirectGamePaths()
    {
        var game = CreateGame(root, "Neon Abyss");
        foreach (var path in new[] { game, Path.Combine(root, "steamapps"), Path.Combine(root, "steamapps", "common") })
            Assert.Single(service.Discover(new[] { path }).Games);
    }

    [Fact]
    public void Il2cppTakesPrecedenceOverManagedFolder()
    {
        var path = CreateGame(root, "AotGame", true);
        var game = service.InspectGame(path)!;
        Assert.Equal("IL2CPP", game.Runtime);
        Assert.False(game.CanUseManaged);
    }

    [Fact]
    public void MultipleDataDirectoriesAreNotAutomaticallySelected()
    {
        var path = CreateGame(root, "Ambiguous");
        Directory.CreateDirectory(Path.Combine(path, "Second_Data", "Managed"));
        Assert.False(service.InspectGame(path)!.CanUseManaged);
    }

    [Fact]
    public void LatestLogAndBoundedTailAreReadWithoutChangingFile()
    {
        var path = CreateGame(root, "LoggingGame");
        var latest = Path.Combine(path, "MelonLoader", "Latest.log");
        File.WriteAllText(latest, "old");
        File.SetLastWriteTimeUtc(latest, DateTime.UtcNow.AddDays(-1));
        var logs = Path.Combine(path, "MelonLoader", "Logs");
        Directory.CreateDirectory(logs);
        var recent = Path.Combine(logs, "new.log");
        File.WriteAllText(recent, new string('x', 100) + "끝부분");
        Assert.Equal(recent, service.InspectGame(path)!.LogPath);
        Assert.Equal("끝부분", service.ReadLogTail(recent, 3));
        Assert.Equal(103, File.ReadAllText(recent).Length);
    }

    [Fact]
    public void InvalidLibraryDoesNotDiscardValidResults()
    {
        CreateGame(root, "Valid");
        var result = service.Discover(new[] { Path.Combine(root, "absent"), root });
        Assert.Single(result.Games);
        Assert.NotEmpty(result.Warnings);
    }

    [Fact]
    public async Task SearchAndActionsConnectExistingTools()
    {
        CreateGame(root, "Neon Abyss");
        CreateGame(root, "Zombie Rollerz");
        var main = new MainWindowViewModel();
        var vm = main.GameDiscovery;
        vm.SearchRoot = root;
        vm.OnlySuspected = false;
        await vm.DiscoverCommand.ExecuteAsync(null);
        vm.SearchQuery = "neon abyss";
        vm.SelectedGame = Assert.Single(vm.FilteredGames);
        vm.UseAsTargetCommand.Execute(null);
        Assert.Equal(vm.SelectedGame.ManagedPath, main.AssemblyDiff.GameManagedPath);
        Assert.Equal(2, main.SelectedTabIndex);
        vm.SearchQuery = "zombie";
        Assert.Null(vm.SelectedGame);
        vm.SelectedGame = Assert.Single(vm.FilteredGames);
        vm.UseAsDonorCommand.Execute(null);
        Assert.Equal(vm.SelectedGame.ManagedPath, main.AssemblyDiff.DonorBclPath);
        Assert.True(vm.IsIdle);
    }

    [Fact]
    public async Task SelectedGameLogLoadsAndNavigatesToDiagnosis()
    {
        var path = CreateGame(root, "LoggingGame");
        File.WriteAllText(Path.Combine(path, "MelonLoader", "Latest.log"), "SIGSEGV");
        var main = new MainWindowViewModel();
        main.GameDiscovery.SelectedGame = service.InspectGame(path);
        await main.GameDiscovery.LoadLogCommand.ExecuteAsync(null);
        Assert.Equal("SIGSEGV", main.LogAnalyzer.LogInputText);
        Assert.True(main.LogAnalyzer.HasDiagnosis);
        Assert.Equal(1, main.SelectedTabIndex);
    }

    [Fact]
    public void AotGameDoesNotOverwriteExistingTarget()
    {
        var path = CreateGame(root, "AotGame", true);
        var main = new MainWindowViewModel();
        main.AssemblyDiff.GameManagedPath = "previous";
        main.GameDiscovery.SelectedGame = service.InspectGame(path);
        main.GameDiscovery.UseAsTargetCommand.Execute(null);
        Assert.Equal("previous", main.AssemblyDiff.GameManagedPath);
        Assert.Contains("IL2CPP", main.GameDiscovery.Status);
    }

    public void Dispose() => Directory.Delete(root, true);
}


