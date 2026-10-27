using GameHelper.Core.Models;
using GameHelper.Core.Services;
using GameHelper.Core.Validation;
using GameHelper.Infrastructure.Providers;
using GameHelper.Infrastructure.Validators;

namespace GameHelper.Tests;

public sealed class GameActionConfigurationTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "gh-actions-" + Guid.NewGuid().ToString("N"));
    private string ConfigPath => Path.Combine(_directory, "config.yml");
    public GameActionConfigurationTests() => Directory.CreateDirectory(_directory);
    public void Dispose() => Directory.Delete(_directory, true);

    [Fact]
    public void Actions_ReadWriteAndCatalogEdit_PreserveOrderAndLegacyCompanion()
    {
        File.WriteAllText(ConfigPath, """
            games:
              - dataKey: game
                executable: game.exe
                launch:
                  target: steam://rungameid/620
                  ds4WindowsPath: C:\Tools\DS4Windows.exe
                actions:
                  - when: beforeLaunch
                    type: run
                    path: C:\Tools\prepare.exe
                    arguments: --ready
                    wait: true
                  - when: afterExit
                    type: close
                    path: C:\Tools\prepare.exe
            """);
        var provider = new YamlConfigProvider(ConfigPath);
        var original = provider.Read().Games.Single();
        Assert.True(YamlConfigValidator.Validate(ConfigPath).IsValid);
        var actions = original.Actions!;
        Assert.Equal("--ready", actions[0].Arguments);
        Assert.True(actions[0].Wait);
        Assert.Null(actions[1].Wait);
        provider.Change(_ => { });
        new GameCatalogService(provider).Update("game", new() { DisplayName = "Renamed" });
        var saved = provider.Read().Games.Single();
        Assert.Equal(actions, saved.Actions);
        Assert.Equal(original.Launch, saved.Launch);
        Assert.Equal(actions, new GameCatalogService(provider).List().Single().Actions);
    }

    [Theory]
    [InlineData("beforeLaunch", "run", "relative.exe")]
    [InlineData("beforeLaunch", "run", "C:relative.exe")]
    [InlineData("beforeLaunch", "run", "C:\\Tools\\run.cmd")]
    [InlineData("beforeLaunch", "unknown", "C:\\Tools\\run.exe")]
    [InlineData("unknown", "run", "C:\\Tools\\run.exe")]
    public void InvalidAction_FileDiagnosticsAndSaveRejectWithoutChangingBytes(string when, string type, string path)
    {
        var provider = new YamlConfigProvider(ConfigPath);
        provider.Change(config => config.Games = [new() { DataKey = "game", Executable = "game.exe" }]);
        var before = File.ReadAllBytes(ConfigPath);
        var action = new GameAction { When = when, Type = type, Path = path };
        Assert.Throws<InvalidDataException>(() => provider.Change(config => config.Games[0].Actions = [action]));
        Assert.Equal(before, File.ReadAllBytes(ConfigPath));
        File.WriteAllText(ConfigPath, $"games:\n  - dataKey: game\n    executable: game.exe\n    actions:\n      - when: {when}\n        type: {type}\n        path: {path}\n");
        var result = YamlConfigValidator.Validate(ConfigPath);
        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, error => error.Contains("actions[1]"));
        Assert.Throws<InvalidDataException>(() => provider.Read());
    }

    [Fact]
    public void Actions_IndependentInvalidEntriesAccumulateDiagnostics()
    {
        var result = AppConfigValidator.Validate(new() { Games = [new() { DataKey = "game", Executable = "game.exe", Actions =
            [new() { When = "invalid" }, new() { Type = "invalid" }, new() { Path = "relative.exe" }] }] });
        Assert.Equal(3, result.Errors.Count);
        Assert.Throws<ArgumentException>(() => new GameAction { When = "beforeLaunch", Type = "close", Path = @"C:\Tool.exe", Wait = false }.Validate());
    }

    [Fact]
    public void Actions_CatalogReplacesAndClearsWithoutChangingLaunch()
    {
        var provider = new YamlConfigProvider(ConfigPath);
        provider.Change(config => config.Games = [new() { DataKey = "game", Executable = "game.exe",
            Launch = new() { Target = "steam://rungameid/620" } }]);
        var catalog = new GameCatalogService(provider);
        var actions = new List<GameAction> { new() { When = "beforeLaunch", Type = "run", Path = @"C:\Tools\tool.exe" } };
        catalog.Update("game", new() { Actions = actions });
        Assert.Equal(actions, provider.Read().Games.Single().Actions);
        catalog.Update("game", new() { ClearActions = true });
        var game = provider.Read().Games.Single();
        Assert.Null(game.Actions);
        Assert.Equal("steam://rungameid/620", game.Launch!.Target);
        Assert.DoesNotContain("actions:", File.ReadAllText(ConfigPath));
    }
}
