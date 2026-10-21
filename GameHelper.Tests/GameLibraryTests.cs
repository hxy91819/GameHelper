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
