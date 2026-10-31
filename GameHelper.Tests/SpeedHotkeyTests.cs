using GameHelper.Core.Abstractions;
using GameHelper.Core.Models;
using GameHelper.Core.Services;
using Moq;
using GameHelper.Core.Utilities;
using GameHelper.Infrastructure.Providers;
using GameHelper.Infrastructure.Validators;

namespace GameHelper.Tests;

public sealed class SpeedHotkeyTests
{
    [Theory]
    [InlineData("Ctrl+F1", 17, 112)]
    [InlineData("alt+A", 18, 65)]
    [InlineData("Shift+9", 16, 57)]
    public void Shortcut_TranslatesToCeKeys(string shortcut, int modifier, int key)
        => Assert.Equal(new[] { modifier, key }, new SpeedHotkey { Hotkey = shortcut }.GetVirtualKeys());

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Ctrl")]
    [InlineData("Ctrl+Ctrl+A")]
    [InlineData("F13")]
    [InlineData("A+B")]
    [InlineData("Ctrl++A")]
    [InlineData("Ctrl+F1);error('injection')")]
    public void Shortcut_RejectsUnsupportedCombinations(string? shortcut)
        => Assert.Throws<ArgumentException>(() => new SpeedHotkey { Hotkey = shortcut! }.GetVirtualKeys());

    [Fact]
    public void Shortcuts_RejectEquivalentKeysAndNonFiniteOrNonPositiveSpeeds()
    {
        Assert.Throws<ArgumentException>(() => SpeedHotkey.Validate([
            new() { Hotkey = "Ctrl+Alt+F1" }, new() { Hotkey = "alt+F1+ctrl" }]));
        foreach (var speed in new[] { double.NaN, double.PositiveInfinity, 0, -1 })
            Assert.Throws<ArgumentException>(() => SpeedHotkey.Validate([new() { Hotkey = "F1", Speed = speed }]));
    }

    [Fact]
    public void Revision_HotkeyChangesInvalidateDraftsAndNullPreservesLegacyToken()
    {
        const string legacy = """{"DataKey":"a","Executable":"a.exe","DisplayName":null,"IsEnabled":true,"HdrEnabled":false,"SpeedMultiplier":2,"SpeedAttach":null,"Launch":null}""";
        var game = System.Text.Json.JsonSerializer.Deserialize<GameConfig>(legacy)!;
        var expected = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(legacy)));
        Assert.Equal(expected, GameConfigRevision.For(game));
        game.SpeedHotkeys = [new() { Hotkey = "F1", Speed = 1 }];
        var before = GameConfigRevision.For(game);
        Assert.NotEqual(expected, before);
        game.SpeedHotkeys[0] = new() { Hotkey = "F1", Speed = 2 };
        Assert.NotEqual(before, GameConfigRevision.For(game));
    }

    [Theory]
    [InlineData("[{hotkey: Ctrl+F1, speed: 2}, {hotkey: F2, speed: 1}, {hotkey: F3}]", true)]
    [InlineData("[]", true)]
    [InlineData("[{hotkey: F1, speed: 0}]", false)]
    [InlineData("[{hotkey: F1, speed: .nan}]", false)]
    [InlineData("[{hotkey: F1, speed: fast}]", false)]
    [InlineData("[{hotkey: Ctrl+F1}, {hotkey: F1+Ctrl}]", false)]
    [InlineData("[{hotkey: Ctrl}]", false)]
    [InlineData("[{speed: 2}]", false)]
    [InlineData("[{hotkey: F1, action: cycle}]", false)]
    [InlineData("{hotkey: F1}", false)]
    public void Yaml_ValidatesHotkeyStructureAndValues(string bindings, bool valid)
    {
        WithConfig(path =>
        {
            File.WriteAllText(path, $"games:\n  - dataKey: game\n    executable: game.exe\n    speed: 2\n    speedHotkeys: {bindings}\n");
            var result = YamlConfigValidator.Validate(path);
            Assert.Equal(valid, result.IsValid);
            if (valid) Assert.Empty(result.Warnings);
        });
    }

    [Fact]
    public void Provider_RoundtripsHotkeysAndCatalogEditsPreserveAndClearThem()
    {
        WithConfig(path =>
        {
            GameHelper.Core.Abstractions.IGameConfiguration provider = new YamlConfigProvider(path);
            provider.Change(config => config.Games = [new()
            {
                DataKey = "game", Executable = "game.exe", SpeedMultiplier = 2,
                SpeedHotkeys = [new() { Hotkey = "Ctrl+F1", Speed = 0.5 }, new() { Hotkey = "F2", Speed = 1 }, new() { Hotkey = "F3" }]
            }]);
            var game = Assert.Single(provider.Read().Games);
            Assert.Equal(new double?[] { 0.5, 1, null }, game.SpeedHotkeys!.Select(binding => binding.Speed));
            var catalog = new GameHelper.Core.Services.GameCatalogService(provider);
            catalog.Update("game", new() { DisplayName = "Renamed" });
            Assert.Equal(game.SpeedHotkeys, catalog.List()[0].SpeedHotkeys);
            catalog.Intake(new() { Executable = ExecutableIdentity.Parse("game.exe"), DisplayName = "Imported again" });
            Assert.Equal(game.SpeedHotkeys, catalog.List()[0].SpeedHotkeys);
            catalog.Update("game", new() { ClearSpeedHotkeys = true });
            Assert.Null(Assert.Single(provider.Read().Games).SpeedHotkeys);
            Assert.Equal(2, Assert.Single(provider.Read().Games).SpeedMultiplier);
        });
    }

    [Fact]
    public void Save_InvalidHotkeysDoesNotOverwriteConfiguration()
    {
        WithConfig(path =>
        {
            var provider = new YamlConfigProvider(path);
            provider.Change(config => config.Games = [new() { DataKey = "game", Executable = "game.exe", SpeedMultiplier = 2 }]);
            var before = File.ReadAllText(path);
            Assert.Throws<InvalidDataException>(() => provider.Change(config =>
                config.Games[0].SpeedHotkeys = [new() { Hotkey = "F1", Speed = -2 }]));
            Assert.Equal(before, File.ReadAllText(path));
        });
    }

    [Fact]
    public async Task LibrarySave_HotkeysPersistRejectStaleDraftAndCanBeCleared()
    {
        var root = Path.Combine(Path.GetTempPath(), "gh-library-hotkeys-" + Guid.NewGuid().ToString("N"));
        try
        {
            var source = new YamlConfigProvider(Path.Combine(root, "config.yml"));
            source.Change(document => document.Games = [new() { DataKey = "game", Executable = "game.exe", SpeedMultiplier = 2 }]);
            var launch = new Mock<IGameLaunchCoordinator>();
            launch.Setup(item => item.GetGameStates()).Returns(new Dictionary<string, string>());
            var history = new Mock<IPlaytimeSnapshotProvider>();
            history.Setup(item => item.GetPlaytimeRecords()).Returns([]);
            history.Setup(item => item.GetSnapshot()).Returns(new PlaytimeSnapshot([], null));
            var library = new GameLibrary(source, new GameCatalogService(source), Mock.Of<IGameAutomationService>(),
                Mock.Of<IMonitorControlService>(), launch.Object, Mock.Of<ISteamGameResolver>(), history.Object);
            var original = (await library.ExecuteAsync(new() { Operation = GameLibraryOperation.List })).Games.Single();
            var edit = new GameConfig { DataKey = "game", Executable = "game.exe", SpeedMultiplier = 2,
                SpeedHotkeys = [new() { Hotkey = "F1", Speed = 1 }, new() { Hotkey = "F2" }] };
            var response = await library.ExecuteAsync(new() { Operation = GameLibraryOperation.Save,
                DataKey = "game", ExpectedRevision = original.Revision, Game = edit });
            Assert.Equal(edit.SpeedHotkeys, response.Games.Single().Config.SpeedHotkeys);
            await Assert.ThrowsAsync<InvalidOperationException>(() => library.ExecuteAsync(new()
            {
                Operation = GameLibraryOperation.Save, DataKey = "game", ExpectedRevision = original.Revision, Game = edit
            }));
            edit.SpeedHotkeys = null;
            await library.ExecuteAsync(new() { Operation = GameLibraryOperation.Save, DataKey = "game",
                ExpectedRevision = response.Games.Single().Revision, Game = edit });
            Assert.Null(source.Read().Games.Single().SpeedHotkeys);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    private static void WithConfig(Action<string> test)
    {
        var root = Path.Combine(Path.GetTempPath(), "gh-hotkeys-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try { test(Path.Combine(root, "config.yml")); }
        finally { Directory.Delete(root, true); }
    }
}
