using System.Text.Json;
using GameHelper.Core.Models;
using GameHelper.Core.Services;
using GameHelper.Core.Validation;
using GameHelper.Infrastructure.Providers;
using GameHelper.Infrastructure.Validators;

namespace GameHelper.Tests;

public sealed class ConfigurationContractTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "GameHelper.ConfigContract", Guid.NewGuid().ToString("N"));
    private string ConfigPath => Path.Combine(_directory, "config.yml");
    public ConfigurationContractTests() => Directory.CreateDirectory(_directory);
    public void Dispose() => Directory.Delete(_directory, true);

    [Fact]
    public void HistoricalConfig_ReadWriteRead_PreservesAllSupportedFields()
    {
        // Verbatim debug.config.yml from 7f04da8, before autoStartMonitor was retired.
        File.WriteAllText(ConfigPath, LegacyConfig);
        var provider = new YamlConfigProvider(ConfigPath);
        var original = provider.Read();
        Assert.Equal(3, original.Games.Count);
        Assert.Equal("Cheat Engine", original.Games[0].DisplayName);
        Assert.True(original.Games[0].HdrEnabled);
        Assert.Equal("Ctrl+F1", original.CheatEngine!.SpeedHotkeys![0].Hotkey);
        Assert.Equal("yourname/game-stats", original.SyncSettings!.Repo);
        Assert.Equal(40, original.BreakReminder!.PlayMinutes);
        Assert.NotNull(original.MonitorMode);
        provider.Change(_ => { });
        Assert.Equal(JsonSerializer.Serialize(original), JsonSerializer.Serialize(provider.Read()));
        Assert.True(YamlConfigValidator.Validate(ConfigPath).IsValid);
        // Legacy unknown keys were already ignored and removed on save; they gain no new meaning.
        Assert.DoesNotContain("autoStartMonitor", File.ReadAllText(ConfigPath));
    }

    [Fact]
    public void CatalogChanges_PreserveEveryUneditedGameField()
    {
        var provider = new YamlConfigProvider(ConfigPath);
        provider.Change(config => config.Games = [new()
        {
            DataKey = "game", Executable = "game.exe", DisplayName = "Game", IsEnabled = false,
            HdrEnabled = true, SpeedMultiplier = 2, SpeedAttach = "immediate",
            SpeedHotkeys = [new() { Hotkey = "Ctrl+F1", Speed = 0.5 }],
            AddedAt = DateTimeOffset.Parse("2026-01-02T03:04:05+08:00"),
            Launch = new() { Target = "steam://rungameid/620", Ds4WindowsPath = @"C:\Tools\DS4Windows.exe" }
        }]);
        var original = provider.Read().Games.Single();
        var catalog = new GameCatalogService(provider);
        catalog.Update("game", new() { DisplayName = "Renamed" });
        original.DisplayName = "Renamed";
        Assert.Equal(JsonSerializer.Serialize(original), JsonSerializer.Serialize(provider.Read().Games.Single()));
        catalog.Intake(new() { Executable = ExecutableIdentity.Parse("game.exe"), IsEnabled = false });
        Assert.Equal(JsonSerializer.Serialize(original), JsonSerializer.Serialize(provider.Read().Games.Single()));
        Assert.True(YamlConfigValidator.Validate(ConfigPath).IsValid);
    }

    [Fact]
    public void LegacyNullScalars_KeepDtoDefaultsAndInvalidOptionalValuesAreIgnoredOnRead()
    {
        File.WriteAllText(ConfigPath, """
monitor: null
startup: {autoStartMonitor: true, launchOnStartup: false}
cheatEngine: {executable: '  ', speedHotkeys: [{hotkey: Ctrl}]}
sync: {enabled: null, provider: null, method: null, repo: null, intervalMinutes: 0}
games:
  - dataKey: game
    executable: game.exe
    enabled: null
    hdr: null
    addedAt: null
    speed: -1
    speedAttach: bad
    speedHotkeys: [{hotkey: Ctrl}]
""");
        var config = new YamlConfigProvider(ConfigPath).Read();
        Assert.Equal(ProcessMonitorType.ETW, config.ProcessMonitorType);
        Assert.Null(config.CheatEngine);
        Assert.Equal("github", config.SyncSettings!.Provider);
        Assert.Equal("git", config.SyncSettings.Method);
        Assert.Equal(SyncSettings.DefaultIntervalMinutes, config.SyncSettings.IntervalMinutes);
        var game = Assert.Single(config.Games);
        Assert.True(game.IsEnabled);
        Assert.False(game.HdrEnabled);
        Assert.Null(game.SpeedMultiplier);
        Assert.Null(game.SpeedAttach);
        Assert.Null(game.SpeedHotkeys);
    }

    [Theory]
    [InlineData("monitor")]
    [InlineData("cheatEngine")]
    [InlineData("sync")]
    public void SettingsUpdate_InvalidGlobalSettings_LeavesExistingDocumentUntouched(string field)
    {
        var provider = new YamlConfigProvider(ConfigPath);
        provider.Change(_ => { });
        var original = File.ReadAllText(ConfigPath);
        var request = new UpdateAppSettingsRequest();
        switch (field)
        {
            case "monitor": request.ProcessMonitorType = (ProcessMonitorType)99; break;
            case "cheatEngine": request.CheatEngine = new() { Executable = "relative.exe" }; break;
            case "sync": request.SyncSettings = new() { Enabled = true, Repo = "invalid" }; break;
        }
        Assert.Throws<ArgumentException>(() => new SettingsService(provider).Update(request));
        Assert.Equal(original, File.ReadAllText(ConfigPath));
    }

    [Theory]
    [InlineData("")]
    [InlineData("null")]
    [InlineData("startup: null\ngames: []")]
    public void LegacyEmptyDocumentAndStartup_ReadWithDefaults(string yaml)
    {
        File.WriteAllText(ConfigPath, yaml);
        var config = new YamlConfigProvider(ConfigPath).Read();
        Assert.Empty(config.Games);
        Assert.Equal(ProcessMonitorType.ETW, config.ProcessMonitorType);
        Assert.False(config.LaunchOnSystemStartup);
        Assert.False(YamlConfigValidator.Validate(ConfigPath).IsValid);
    }

    [Theory]
    [InlineData("speed: 0", 0d, null)]
    [InlineData("speedAttach: invalid", null, "invalid")]
    public void FileAndTransaction_UseTheSameGameRules(string yaml, double? speed, string? policy)
    {
        var config = new AppConfig { Games = [new() { DataKey = "game", Executable = "game.exe", SpeedMultiplier = speed, SpeedAttach = policy }] };
        File.WriteAllText(ConfigPath, $"games:\n  - dataKey: game\n    executable: game.exe\n    {yaml}\n");
        var fileResult = YamlConfigValidator.Validate(ConfigPath);
        Assert.False(fileResult.IsValid);
        Assert.Equal(AppConfigValidator.Validate(config).Errors, fileResult.Errors);
        File.Delete(ConfigPath);
        var provider = new YamlConfigProvider(ConfigPath);
        Assert.Throws<InvalidDataException>(() => provider.Change(current => current.Games = config.Games));
        Assert.False(File.Exists(ConfigPath));
    }

    [Theory]
    [InlineData("cheatEngine: {executable: relative.exe}", true)]
    [InlineData("sync: {enabled: true, repo: invalid}", true)]
    [InlineData("", false)]
    public void LegacySaveScopes_PreserveUnrelatedInvalidConfiguration(string globals, bool settingsRejects)
    {
        File.WriteAllText(ConfigPath, globals + "\ngames:\n  - dataKey: sample\n    executable: sample.exe\n" + (settingsRejects ? "" : "    launch: {}\n"));
        var provider = new YamlConfigProvider(ConfigPath);
        provider.Change(config => config.Games[0].DisplayName = "Renamed");
        Assert.Equal("Renamed", provider.Read().Games[0].DisplayName);
        Assert.Equal(!settingsRejects, provider.Read().Games[0].Launch is not null);
        var original = File.ReadAllBytes(ConfigPath);
        var service = new SettingsService(provider);
        var request = new UpdateAppSettingsRequest
        {
            ProcessMonitorType = ProcessMonitorType.WMI, LaunchOnSystemStartup = true,
            BreakReminder = new() { PlayMinutes = 30 }
        };
        if (settingsRejects)
        {
            Assert.Throws<ArgumentException>(() => service.Update(request));
            Assert.Equal(original, File.ReadAllBytes(ConfigPath));
        }
        else
        {
            service.Update(request);
            Assert.Equal(ProcessMonitorType.WMI, provider.Read().ProcessMonitorType);
            Assert.NotNull(provider.Read().Games[0].Launch);
        }
    }

    [Fact]
    public void Validate_MultipleNullErrors_CollectsAllFiveAndCountsGames()
    {
        File.WriteAllText(ConfigPath, """
startup: null
hdr: null
breakReminder: null
games:
  - dataKey: sample
    executable: sample.exe
    enabled: null
    hdr: null
""");
        var result = YamlConfigValidator.Validate(ConfigPath);
        Assert.False(result.IsValid);
        Assert.Equal(5, result.Errors.Count);
        Assert.Contains(result.Errors, error => error.Contains("startup"));
        Assert.Contains(result.Errors, error => error.StartsWith("hdr"));
        Assert.Contains(result.Errors, error => error.Contains("breakReminder"));
        Assert.Contains(result.Errors, error => error.Contains("enabled"));
        Assert.Contains(result.Errors, error => error.Contains("games[1].hdr"));
        Assert.Equal(1, result.GameCount);
    }

    [Theory]
    [InlineData("monitor: invalid")]
    [InlineData("startup: false")]
    public void Validate_InvalidGlobalShape_StillCollectsGameErrorsAndCounts(string globals)
    {
        File.WriteAllText(ConfigPath, globals + "\ngames:\n  - dataKey: sample\n    executable: sample.exe\n    launch: {}\n");
        var result = YamlConfigValidator.Validate(ConfigPath);
        Assert.False(result.IsValid);
        Assert.Equal(1, result.GameCount);
        Assert.Equal(2, result.Errors.Count);
        Assert.Contains("Game 'sample': launch.target is required.", result.Errors);
    }

    [Theory]
    [InlineData("speedAttach: nonsense")]
    [InlineData("addedAt: yesterday")]
    public void Validate_StrongerValueChecks_RemainInvalid(string field)
    {
        File.WriteAllText(ConfigPath, "games:\n  - dataKey: sample\n    executable: sample.exe\n    " + field + "\n");
        Assert.False(YamlConfigValidator.Validate(ConfigPath).IsValid);
    }

    [Theory]
    [InlineData("debug")]
    [InlineData("full")]
    public void EmptyChange_WritesExactLegacyBytes(string sample)
    {
        // Expected files were generated by f049f6b's YamlConfigProvider.Change(_ => {}).
        byte[] Resource(string suffix)
        {
            using var stream = typeof(ConfigurationContractTests).Assembly.GetManifestResourceStream(
                $"GameHelper.Tests.Resources.ConfigurationCompatibility.{sample}.{suffix}.yml")!;
            using var bytes = new MemoryStream();
            stream.CopyTo(bytes);
            return bytes.ToArray();
        }
        File.WriteAllBytes(ConfigPath, Resource("input"));
        new YamlConfigProvider(ConfigPath).Change(_ => { });
        Assert.Equal(Resource("expected"), File.ReadAllBytes(ConfigPath));
    }

    private const string LegacyConfig = """
# addedAt 为工具新增游戏时记录的可选 ISO 8601 时间；旧条目无需补填，编辑或重复导入不会重置。
# GameHelper debug configuration
monitor: ETW
startup:
  autoStartMonitor: false
  launchOnStartup: false
cheatEngine:
  executable: C:\Program Files\Cheat Engine\cheatengine-x86_64-SSE4-AVX2.exe
  # 可选：未单独配置 speedHotkeys 的倍速游戏使用这套默认热键
  speedHotkeys:
    - hotkey: Ctrl+F1
      speed: 1.0
    - hotkey: Ctrl+F2   # 省略 speed：在主倍速与原速间切换
# 统计推送示例（默认关闭；完整字段见 docs/guides/sync.md）
sync:
  enabled: false
  provider: github
  method: git
  repo: yourname/game-stats
  directory: game-stats
  intervalMinutes: 1440
# 休息提醒默认关闭；完整规则见 docs/guides/break-reminder.md。
breakReminder:
  enabled: false
  playMinutes: 40
  breakMinutes: 5
  repeatMinutes: 10
  sound: true
# 显示器预设自动切换默认关闭；完整规则见 docs/guides/monitor-mode.md。
# U2720QM 默认护眼为单笔 F0=0x0C；其它型号可覆盖 comfortPresets。
# HDR 开启或状态无法确认时自动跳过 DDC；关闭后恢复规则。
monitorMode:
  enabled: false
  # location: auto        # 自动定位取坐标（需系统“定位”开关开启；与经纬度二选一）
  # latitude: 39.907      # 手动坐标优先级最高；两者都不配则用固定时间窗
  # longitude: 116.391
  # daylightStart: "07:00"
  # daylightEnd: "18:30"
games:
  - dataKey: cheatengine
    executable: Cheat Engine.exe
    displayName: Cheat Engine
    enabled: true
    hdr: true
  - dataKey: project_plague
    executable: Project_Plague.exe
    displayName: Project Plague
    enabled: true
    hdr: false
    # speed: 2.0
    # speedAttach: immediate
    # launch:
    #   target: C:\Games\Project_Plague.exe
    #   arguments: -windowed
    #   workingDirectory: C:\Games
    #   ds4WindowsPath: C:\Tools\DS4Windows\DS4Windows.exe # 可选，启动前运行，已运行时复用
  - dataKey: talesofarise
    executable: Tales of Arise.exe
    displayName: Tales of Arise
    enabled: true
    hdr: false
""";
}
