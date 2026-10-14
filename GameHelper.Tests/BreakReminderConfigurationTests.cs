using GameHelper.Core.Abstractions;
using GameHelper.Core.Models;
using GameHelper.Core.Services;
using GameHelper.Infrastructure.Providers;
using GameHelper.Infrastructure.Validators;

namespace GameHelper.Tests;

public sealed class BreakReminderConfigurationTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "GameHelper.BreakReminder.Tests", Guid.NewGuid().ToString("N"));

    public BreakReminderConfigurationTests() => Directory.CreateDirectory(_directory);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ConfigurationChanges_PreserveReminderAlongsideCatalogStartupAndSpeed(bool json)
    {
        IGameConfiguration provider = json
            ? new JsonConfigProvider(Path.Combine(_directory, "config.json"))
            : new YamlConfigProvider(Path.Combine(_directory, "config.yml"));
        var settings = new BreakReminderSettings { Enabled = true, PlayMinutes = 30, BreakMinutes = 6, RepeatMinutes = 8, Sound = false };
        provider.Change(config =>
        {
            config.BreakReminder = settings;
            config.CheatEngine = new CheatEngineSettings { Executable = @"C:\Tools\cheatengine.exe" };
            config.Games.Add(new GameConfig { DataKey = "game", Executable = "game.exe", SpeedMultiplier = 2 });
        });
        new SettingsService(provider).Update(new UpdateAppSettingsRequest { AutoStartInteractiveMonitor = true });
        provider.Change(config => config.Games[0].DisplayName = "Game");
        var saved = provider.Read();
        Assert.Equal(settings, saved.BreakReminder);
        Assert.Equal(2, Assert.Single(saved.Games).SpeedMultiplier);
        Assert.Equal(@"C:\Tools\cheatengine.exe", saved.CheatEngine?.Executable);
        Assert.True(saved.AutoStartInteractiveMonitor);
    }

    [Fact]
    public void Yaml_AbsentOrPartialSettings_UsesOptInAndDurationDefaults()
    {
        var path = Path.Combine(_directory, "config.yml");
        File.WriteAllText(path, "games: []\n");
        var provider = new YamlConfigProvider(path);
        Assert.Null(provider.Read().BreakReminder);
        File.WriteAllText(path, "breakReminder: {}\ngames: []\n");
        Assert.Equal(new BreakReminderSettings(), provider.Read().BreakReminder);
        File.WriteAllText(path, "breakReminder:\n  enabled: true\ngames: []\n");
        Assert.Equal(new BreakReminderSettings { Enabled = true }, provider.Read().BreakReminder);
        provider.Change(config => config.AutoStartInteractiveMonitor = true);
        Assert.Equal(new BreakReminderSettings { Enabled = true }, provider.Read().BreakReminder);
    }

    [Theory]
    [InlineData("playMinutes", "0")]
    [InlineData("breakMinutes", "-1")]
    [InlineData("repeatMinutes", "1.5")]
    [InlineData("playMinutes", "word")]
    [InlineData("repeatMinutes", "2147483648")]
    [InlineData("enabled", "yes")]
    [InlineData("sound", "[]")]
    public void Validator_InvalidReminderField_ReportsErrorWithoutGamesSection(string field, string value)
    {
        var path = Path.Combine(_directory, "config.yml");
        File.WriteAllText(path, $"breakReminder:\n  {field}: {value}\n");
        var result = YamlConfigValidator.Validate(path);
        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, error => error.Contains($"breakReminder.{field}", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("true")]
    [InlineData("null")]
    public void Validator_InvalidReminderShape_ReportsError(string value)
    {
        var path = Path.Combine(_directory, "config.yml");
        File.WriteAllText(path, $"breakReminder: {value}\ngames: []\n");
        Assert.False(YamlConfigValidator.Validate(path).IsValid);
    }

    [Fact]
    public void Validator_ValidReminderAndUnknownField_ValidWithWarning()
    {
        var path = Path.Combine(_directory, "config.yml");
        File.WriteAllText(path, "breakReminder:\n  enabled: true\n  playMinutes: 40\n  breakMinutes: 5\n  repeatMinutes: 10\n  sound: true\n  unknown: 1\ngames: []\n");
        var result = YamlConfigValidator.Validate(path);
        Assert.True(result.IsValid);
        Assert.Contains(result.Warnings, warning => warning.Contains("unknown", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void InvalidDuration_CannotCommitAndLeavesOriginalDocumentIntact(bool json)
    {
        var path = Path.Combine(_directory, json ? "config.json" : "config.yml");
        IGameConfiguration provider = json ? new JsonConfigProvider(path) : new YamlConfigProvider(path);
        provider.Change(config => config.BreakReminder = new BreakReminderSettings { Enabled = true });
        var original = File.ReadAllText(path);
        Assert.Throws<ArgumentException>(() => provider.Change(config =>
            config.BreakReminder = new BreakReminderSettings { Enabled = true, BreakMinutes = 0 }));
        Assert.Equal(original, File.ReadAllText(path));
    }

    public void Dispose() => Directory.Delete(_directory, recursive: true);
}
