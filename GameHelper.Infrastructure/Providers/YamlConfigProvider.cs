using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using GameHelper.Core.Abstractions;
using GameHelper.Core.Models;
using GameHelper.Core.Utilities;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using YamlDotNet.Core;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace GameHelper.Infrastructure.Providers
{
    /// <summary>
    /// YAML-based config provider stored at %AppData%/GameHelper/config.yml.
    /// Supports both legacy game-only configuration and AppConfig format with global settings.
    /// </summary>
    public sealed class YamlConfigProvider : IConfigProvider, IConfigPathProvider, IAppConfigProvider
    {
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

        private AppConfig? _cachedAppConfig;
        private DateTime _lastWriteTime;

        private static IDeserializer Deserializer => new DeserializerBuilder()
            .WithNamingConvention(CamelCaseNamingConvention.Instance)
            .IgnoreUnmatchedProperties()
            .Build();

        private static ISerializer Serializer => new SerializerBuilder()
            .WithNamingConvention(CamelCaseNamingConvention.Instance)
            .ConfigureDefaultValuesHandling(DefaultValuesHandling.OmitNull)
            .WithIndentedSequences()
            .WithNewLine("\n")
            .Build();

        public IReadOnlyDictionary<string, GameConfig> Load()
        {
            var appConfig = LoadAppConfig();
            var result = new Dictionary<string, GameConfig>(StringComparer.OrdinalIgnoreCase);

            if (appConfig.Games is null || appConfig.Games.Count == 0)
            {
                return result;
            }

            var usedEntryIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var usedDataKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var source in appConfig.Games)
            {
                if (source is null)
                {
                    continue;
                }

                var normalized = NormalizeLoadedConfig(source);
                if (normalized is null)
                {
                    continue;
                }

                var originalEntryId = normalized.EntryId;
                normalized.EntryId = ConfigIdentity.EnsureUniqueEntryId(normalized.EntryId, usedEntryIds);
                if (!string.Equals(originalEntryId, normalized.EntryId, StringComparison.OrdinalIgnoreCase))
                {
                    _logger.LogWarning("Detected duplicate or missing EntryId '{EntryId}', regenerated to '{NewEntryId}'.", originalEntryId, normalized.EntryId);
                }

                var originalDataKey = normalized.DataKey;
                normalized.DataKey = ConfigIdentity.EnsureUniqueDataKey(normalized.DataKey, usedDataKeys);
                if (!string.Equals(originalDataKey, normalized.DataKey, StringComparison.OrdinalIgnoreCase))
                {
                    _logger.LogWarning("Detected duplicate DataKey '{DataKey}', adjusted to '{NewDataKey}'.", originalDataKey, normalized.DataKey);
                }

                result[normalized.EntryId] = normalized;
            }

            return result;
        }

        public AppConfig LoadAppConfig()
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
                    _cachedAppConfig = null;
                    return new AppConfig
                    {
                        Games = new List<GameConfig>(),
                        ProcessMonitorType = ProcessMonitorType.ETW
                    };
                }

                var currentWriteTime = File.GetLastWriteTimeUtc(_configFilePath);
                if (_cachedAppConfig != null && currentWriteTime == _lastWriteTime)
                {
                    return _cachedAppConfig.Clone();
                }

                var yaml = File.ReadAllText(_configFilePath);

                try
                {
                    var appConfig = Deserializer.Deserialize<AppConfig?>(yaml);
                    if (appConfig != null)
                    {
                        appConfig.ProcessMonitorType ??= ProcessMonitorType.ETW;
                        _cachedAppConfig = appConfig;
                        _lastWriteTime = currentWriteTime;
                        return appConfig.Clone();
                    }
                }
                catch
                {
                    // fall through to legacy root parsing
                }

                var legacyRoot = Deserializer.Deserialize<LegacyRoot?>(yaml);
                var newConfig = new AppConfig
                {
                    Games = legacyRoot?.Games ?? new List<GameConfig>(),
                    ProcessMonitorType = ProcessMonitorType.ETW
                };

                _cachedAppConfig = newConfig;
                _lastWriteTime = currentWriteTime;

                return newConfig.Clone();
            }
            catch (YamlException ex)
            {
                throw new InvalidDataException("Failed to deserialize config file. Please check its format.", ex);
            }
        }

        public void Save(IReadOnlyDictionary<string, GameConfig> configs)
        {
            var appConfig = LoadAppConfig();

            var normalizedGames = configs.Values
                .Select(NormalizeForSave)
                .Where(c => c is not null)
                .Select(c => c!)
                .ToList();

            RepairDuplicateIdentities(normalizedGames);

            appConfig.Games = normalizedGames;
            SaveAppConfig(appConfig);
        }

        public void SaveAppConfig(AppConfig appConfig)
        {
            var dir = Path.GetDirectoryName(_configFilePath);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }

            var yaml = Serializer.Serialize(appConfig);
            File.WriteAllText(_configFilePath, yaml);
        }

        private static string ResolveDefaultPath() => AppDataPath.GetConfigPath();

        private GameConfig? NormalizeLoadedConfig(GameConfig source)
        {
            var entryId = ConfigIdentity.EnsureEntryId(source.EntryId);
            var executableName = (source.ExecutableName ?? source.Name ?? string.Empty).Trim();
            var executablePath = (source.ExecutablePath ?? string.Empty).Trim();
            var displayName = (source.DisplayName ?? source.Alias ?? string.Empty).Trim();
            var dataKey = (source.DataKey ?? string.Empty).Trim();

            if (string.IsNullOrWhiteSpace(dataKey))
            {
                if (!string.IsNullOrWhiteSpace(executableName))
                {
                    dataKey = executableName;
                    _logger.LogWarning("Config entry missing DataKey; fallback to ExecutableName='{ExecutableName}'.", executableName);
                }
                else if (!string.IsNullOrWhiteSpace(displayName))
                {
                    dataKey = displayName;
                    _logger.LogWarning("Config entry missing DataKey; fallback to DisplayName='{DisplayName}'.", displayName);
                }
                else
                {
                    _logger.LogWarning("Skip config entry: DataKey/ExecutableName/DisplayName are all missing.");
                    return null;
                }
            }

            if (string.IsNullOrWhiteSpace(executablePath) && !string.IsNullOrWhiteSpace(executableName))
            {
                _logger.LogWarning("Config entry '{DataKey}' has no ExecutablePath; fallback matching will use ExecutableName only.", dataKey);
            }

            return new GameConfig
            {
                EntryId = entryId,
                DataKey = dataKey,
                ExecutableName = string.IsNullOrWhiteSpace(executableName) ? null : executableName,
                ExecutablePath = string.IsNullOrWhiteSpace(executablePath) ? null : executablePath,
                DisplayName = string.IsNullOrWhiteSpace(displayName) ? null : displayName,
                IsEnabled = source.IsEnabled,
                HDREnabled = source.HDREnabled
            };
        }

        private GameConfig? NormalizeForSave(GameConfig source)
        {
            var entryId = ConfigIdentity.EnsureEntryId(source.EntryId);
            var dataKey = (source.DataKey ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(dataKey))
            {
                throw new InvalidDataException("Cannot save config entry without DataKey.");
            }

            var executableName = (source.ExecutableName ?? source.Name ?? string.Empty).Trim();
            var executablePath = (source.ExecutablePath ?? string.Empty).Trim();
            var displayName = (source.DisplayName ?? source.Alias ?? string.Empty).Trim();

            if (string.IsNullOrWhiteSpace(executablePath) && !string.IsNullOrWhiteSpace(executableName))
            {
                _logger.LogWarning("Config entry '{DataKey}' is saved without ExecutablePath; fallback matching will use ExecutableName only.", dataKey);
            }

            return new GameConfig
            {
                EntryId = entryId,
                DataKey = dataKey,
                ExecutableName = string.IsNullOrWhiteSpace(executableName) ? null : executableName,
                ExecutablePath = string.IsNullOrWhiteSpace(executablePath) ? null : executablePath,
                DisplayName = string.IsNullOrWhiteSpace(displayName) ? null : displayName,
                IsEnabled = source.IsEnabled,
                HDREnabled = source.HDREnabled
            };
        }

        private void RepairDuplicateIdentities(List<GameConfig> games)
        {
            var usedEntryIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var usedDataKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            for (var i = 0; i < games.Count; i++)
            {
                var config = games[i];

                var originalEntryId = config.EntryId;
                config.EntryId = ConfigIdentity.EnsureUniqueEntryId(config.EntryId, usedEntryIds);
                if (!string.Equals(originalEntryId, config.EntryId, StringComparison.OrdinalIgnoreCase))
                {
                    _logger.LogWarning("Adjusted duplicate EntryId '{EntryId}' to '{NewEntryId}' while saving.", originalEntryId, config.EntryId);
                }

                var originalDataKey = config.DataKey;
                config.DataKey = ConfigIdentity.EnsureUniqueDataKey(config.DataKey, usedDataKeys);
                if (!string.Equals(originalDataKey, config.DataKey, StringComparison.OrdinalIgnoreCase))
                {
                    _logger.LogWarning("Adjusted duplicate DataKey '{DataKey}' to '{NewDataKey}' while saving.", originalDataKey, config.DataKey);
                }
            }
        }

        private sealed class LegacyRoot
        {
            public List<GameConfig>? Games { get; set; }
        }
    }
}
