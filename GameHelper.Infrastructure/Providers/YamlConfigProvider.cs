using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Linq;
using GameHelper.Core.Abstractions;
using GameHelper.Core.Models;
using GameHelper.Core.Utilities;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using YamlDotNet.Core;
using GameHelper.Core.Validation;

namespace GameHelper.Infrastructure.Providers;

/// <summary>
/// YAML-based config provider stored at %AppData%/GameHelper/config.yml.
/// </summary>
public sealed class YamlConfigProvider : IGameConfiguration, IConfigPathProvider
{
    private readonly object _gate = new();
    private readonly string _configFilePath;
    private readonly ILogger<YamlConfigProvider> _logger;

    public YamlConfigProvider()
        : this(ResolveDefaultPath(), NullLogger<YamlConfigProvider>.Instance)
    {
    }

    public YamlConfigProvider(ILogger<YamlConfigProvider> logger)
        : this(ResolveDefaultPath(), logger)
    {
    }

    public YamlConfigProvider(string configFilePath)
        : this(configFilePath, NullLogger<YamlConfigProvider>.Instance)
    {
    }

    public YamlConfigProvider(string configFilePath, ILogger<YamlConfigProvider> logger)
    {
        if (string.IsNullOrWhiteSpace(configFilePath))
        {
            throw new ArgumentException("configFilePath required", nameof(configFilePath));
        }

        _configFilePath = configFilePath;
        _logger = logger ?? NullLogger<YamlConfigProvider>.Instance;
    }

    public string ConfigPath => _configFilePath;

    public AppConfig Read()
    {
        lock (_gate)
        {
            return Normalize(ReadCore());
        }
    }

    public AppConfig Change(Action<AppConfig> change)
    {
        ArgumentNullException.ThrowIfNull(change);

        lock (_gate)
        {
            var appConfig = Normalize(ReadCore());
            change(appConfig);
            appConfig = Normalize(appConfig, forSave: true);
            WriteCore(appConfig);
            return Normalize(ReadCore());
        }
    }

    private AppConfig ReadCore()
    {
        try
        {
            var dir = Path.GetDirectoryName(_configFilePath);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }

            if (!File.Exists(_configFilePath))
            {
                return new AppConfig
                {
                    Games = new List<GameConfig>(),
                    ProcessMonitorType = ProcessMonitorType.ETW
                };
            }

            var yaml = File.ReadAllText(_configFilePath, Encoding.UTF8);

            return NormalizeGlobals(YamlConfigCodec.Deserialize(yaml));
        }
        catch (YamlException ex)
        {
            throw CreateInvalidConfigException(ex);
        }
    }

    private AppConfig Normalize(AppConfig appConfig, bool forSave = false)
    {
        appConfig.Hdr?.Validate();
        if (forSave) appConfig.Report?.Validate();
        if (forSave) appConfig.BreakReminder?.Validate();
        if (forSave) appConfig.MonitorMode?.Validate();

        var normalizedGames = new List<GameConfig>();
        foreach (var source in appConfig.Games ?? Enumerable.Empty<GameConfig>())
        {
            var normalized = forSave
                ? ConfigEntryNormalizer.NormalizeForSave(source, _logger)
                : ConfigEntryNormalizer.NormalizeLoaded(source, _logger);
            if (normalized is not null)
            {
                normalizedGames.Add(normalized);
            }
        }

        ConfigEntryNormalizer.RepairDuplicateDataKeys(
            normalizedGames,
            _logger,
            forSave ? " while saving." : " while loading.");

        appConfig.Games = normalizedGames;
        return appConfig;
    }

    private void WriteCore(AppConfig appConfig)
    {
        var dir = Path.GetDirectoryName(_configFilePath);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        var yaml = YamlConfigCodec.Serialize(PrepareForWrite(appConfig));
        var tempPath = _configFilePath + ".tmp";
        try
        {
            File.WriteAllText(tempPath, yaml);
            File.Move(tempPath, _configFilePath, overwrite: true);
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                File.Delete(tempPath);
            }
        }
    }

    private static string ResolveDefaultPath() => AppDataPath.GetConfigPath();

    private InvalidDataException CreateInvalidConfigException(YamlException ex)
    {
        var location = ex.Start.Line > 0
            ? $" at line {ex.Start.Line}, column {ex.Start.Column}"
            : string.Empty;

        return new InvalidDataException(
            $"Failed to parse config file '{_configFilePath}'{location}. Check YAML syntax; quote string values that contain ': ' (for example displayName: \"Granblue Fantasy: Relink\").",
            ex);
    }

    private static AppConfig NormalizeGlobals(AppConfig config)
    {
        if (config.CheatEngine is { } ce)
        {
            ce.Executable = string.IsNullOrWhiteSpace(ce.Executable) ? null : ce.Executable.Trim();
            try { SpeedHotkey.Validate(ce.SpeedHotkeys); }
            catch (ArgumentException) { ce.SpeedHotkeys = null; }
            if (ce.SpeedHotkeys is { Count: 0 }) ce.SpeedHotkeys = null;
            if (ce.Executable is null && ce.SpeedHotkeys is null) config.CheatEngine = null;
        }
        if (config.SyncSettings is { } sync)
        {
            sync.Provider ??= "github";
            sync.Method ??= "git";
            sync.Repo ??= string.Empty;
            if (string.IsNullOrWhiteSpace(sync.Directory)) sync.Directory = SyncSettings.DefaultDirectory;
            if (sync.IntervalMinutes <= 0) sync.IntervalMinutes = SyncSettings.DefaultIntervalMinutes;
        }
        return config;
    }

    private static AppConfig PrepareForWrite(AppConfig config)
    {
        if (config.CheatEngine is { } ce)
        {
            ce.Executable = string.IsNullOrWhiteSpace(ce.Executable) ? null : ce.Executable.Trim();
            if (ce.SpeedHotkeys is { Count: 0 }) ce.SpeedHotkeys = null;
            if (ce.Executable is null && ce.SpeedHotkeys is null) config.CheatEngine = null;
        }
        if (config.SyncSettings is { } sync)
        {
            sync.Method = sync.NormalizedMethod;
            sync.Repo = sync.NormalizedRepo;
            sync.Branch = sync.NormalizedBranch;
            sync.Directory = sync.NormalizedDirectory;
            if (string.IsNullOrWhiteSpace(sync.Token)) sync.Token = null;
        }
        config.Games = config.Games.OrderBy(game => game.DataKey, StringComparer.OrdinalIgnoreCase).ToList();
        return config;
    }
}
