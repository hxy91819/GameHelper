using GameHelper.Core.Abstractions;
using GameHelper.Core.Models;
using GameHelper.Core.Services;
using GameHelper.Infrastructure.Ipc;
using GameHelper.Infrastructure.Providers;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using GameHelper.ConsoleHost.Services;

namespace GameHelper.Tests;

public sealed class GameLibraryTests
{
    [Fact]
    public void CatalogAndYaml_PreserveLaunchOptionsAndRejectStaleEditsAtomically()
    {
        var root = Path.Combine(Path.GetTempPath(), "GameHelper.Library.Tests." + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(root, "config.yml");
        try
        {
            var source = new YamlConfigProvider(path);
            source.Change(config => config.Games = [new()
            {
                DataKey = "game", Executable = "game.exe", SpeedAttach = "immediate",
                Launch = new() { Target = "steam://rungameid/123", Arguments = null }
            }]);
            var revision = GameHelper.Core.Utilities.GameConfigRevision.For(source.Read().Games.Single());
            var catalog = new GameCatalogService(source);
            catalog.Intake(new GameCatalogIntakeRequest { Executable = ExecutableIdentity.Parse("game.exe"), DisplayName = "Renamed" });
            catalog.Update("game", new GameCatalogUpdateRequest { HdrEnabled = true });
            var saved = source.Read().Games.Single();
            Assert.Equal("steam://rungameid/123", saved.Launch!.Target);
            Assert.Equal("immediate", saved.SpeedAttach);
            var validation = GameHelper.Infrastructure.Validators.YamlConfigValidator.Validate(path);
            Assert.True(validation.IsValid, string.Join("; ", validation.Errors));
            Assert.DoesNotContain(validation.Warnings, warning => warning.Contains("launch", StringComparison.OrdinalIgnoreCase));
            var before = File.ReadAllText(path);
            Assert.Throws<InvalidOperationException>(() => catalog.Update("game", new GameCatalogUpdateRequest { DisplayName = "stale", ExpectedRevision = revision }));
            Assert.Equal(before, File.ReadAllText(path));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public async Task SteamImport_PreservesExistingPreferencesAndUsesPlatformEntryForNewGame()
    {
        var source = new MemoryConfiguration(new AppConfig { Games = [new() { DataKey = "old", DisplayName = "Custom name", Executable = "old.exe", IsEnabled = false, HdrEnabled = true, SpeedMultiplier = 2 }] });
        var steam = new Mock<ISteamGameResolver>();
        steam.Setup(resolver => resolver.TryEnumerateInstalledGames()).Returns([
            new() { AppId = "1", Name = "Old", ExecutablePath = @"C:\old.exe" },
            new() { AppId = "2", Name = "New", ExecutablePath = @"C:\new.exe" }]);
        var launch = new Mock<IGameLaunchCoordinator>();
        launch.Setup(item => item.GetGameStates()).Returns(new Dictionary<string, string>());
        var library = new GameLibrary(source, new GameCatalogService(source), Mock.Of<IGameAutomationService>(),
            Mock.Of<IMonitorControlService>(), launch.Object, steam.Object, History());
        var response = await library.ExecuteAsync(new GameLibraryRequest { Operation = GameLibraryOperation.ImportSteam, SteamAppIds = ["1", "2"] });
        Assert.True(response.Success);
        Assert.Equal(2, source.Read().Games.Count);
        var old = source.Read().Games.Single(game => game.DataKey == "old");
        Assert.False(old.IsEnabled); Assert.True(old.HdrEnabled); Assert.Equal(2, old.SpeedMultiplier);
        Assert.Equal("Custom name", old.DisplayName); Assert.Equal("old.exe", old.Executable); Assert.Null(old.Launch);
        Assert.Equal("steam://rungameid/2", source.Read().Games.Single(game => game.ExecutablePath == @"C:\new.exe").Launch!.Target);
    }

    [Fact]
    public async Task NamedPipe_RoundtripsRequestsAndCanStopWhileIdle()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "config.yml");
        var config = new Mock<IConfigPathProvider>(); config.SetupGet(item => item.ConfigPath).Returns(path);
        var library = new Mock<IGameLibrary>();
        library.Setup(item => item.ExecuteAsync(It.IsAny<GameLibraryRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new GameLibraryResponse { Success = true, Message = "roundtrip" });
        using var server = new GameLibraryServer(library.Object, config.Object, NullLogger<GameLibraryServer>.Instance);
        await server.StartAsync(CancellationToken.None);
        try
        {
            var response = await new GameLibraryPipe(path).ExecuteAsync(new GameLibraryRequest { Operation = GameLibraryOperation.List });
            Assert.True(response.Success); Assert.Equal("roundtrip", response.Message);
        }
        finally { await server.StopAsync(CancellationToken.None); }
    }

    [Fact]
    public async Task PipeDecoder_RejectsUnboundedLengthBeforeAllocatingPayload()
    {
        using var stream = new MemoryStream(BitConverter.GetBytes(int.MaxValue));
        await Assert.ThrowsAsync<InvalidDataException>(() => GameLibraryPipe.ReadAsync<GameLibraryRequest>(stream, CancellationToken.None));
    }

    [Fact]
    public async Task Statistics_DistinguishesMissingHistoryFromReadFailureAndCanRecover()
    {
        var root = Path.Combine(Path.GetTempPath(), "GameHelper.Statistics.Read." + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var provider = new FilePlaytimeSnapshotProvider(root);
            var source = new MemoryConfiguration(new AppConfig());
            var library = new GameLibrary(source, Mock.Of<IGameCatalogService>(), Mock.Of<IGameAutomationService>(),
                Mock.Of<IMonitorControlService>(), Mock.Of<IGameLaunchCoordinator>(), Mock.Of<ISteamGameResolver>(), provider);
            var request = new GameLibraryRequest { Operation = GameLibraryOperation.Statistics, Statistics = new(0) };
            var empty = await library.ExecuteAsync(request);
            Assert.True(empty.Success);
            Assert.Equal(0, empty.Statistics!.Sessions);
            var path = Path.Combine(root, "playtime.csv");
            File.WriteAllText(path, "game,start_time,end_time,duration_minutes\n" +
                "game.exe,2020-01-01T10:00:00,2020-01-01T10:30:00,30\n");
            var loaded = await library.ExecuteAsync(request);
            Assert.True(loaded.Success);
            Assert.Equal(30, loaded.Statistics!.Minutes);
            using (var locked = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                Assert.True(provider.GetSnapshot().ReadFailed);
                Assert.Empty(provider.GetPlaytimeRecords()); // Existing shell fallback remains resilient.
                var failed = await library.ExecuteAsync(request);
                Assert.False(failed.Success);
                Assert.Null(failed.Statistics);
                Assert.Contains("刷新重试", failed.Error);
            }
            var recovered = await library.ExecuteAsync(request);
            Assert.True(recovered.Success);
            Assert.Equal(30, recovered.Statistics!.Minutes);
            Assert.False(provider.GetSnapshot().ReadFailed);
        }
        finally { Directory.Delete(root, true); }
    }

    private static IPlaytimeSnapshotProvider History()
    {
        var history = new Mock<IPlaytimeSnapshotProvider>();
        history.Setup(item => item.GetPlaytimeRecords()).Returns([]);
        return history.Object;
    }
    private sealed class MemoryConfiguration(AppConfig config) : IGameConfiguration
    {
        public AppConfig Read() => config;
        public AppConfig Change(Action<AppConfig> change) { change(config); return config; }
    }
}
