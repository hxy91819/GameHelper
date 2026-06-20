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

            var normalizedGames = new List<GameConfig>();
            foreach (var source in appConfig.Games)
            {
                if (source is null)
                {
                    continue;
                }

                var normalized = ConfigEntryNormalizer.NormalizeLoaded(source, MissingDataKeyAction.Skip, _logger);
                if (normalized is null)
                {
                    continue;
                }

                normalizedGames.Add(normalized);
            }

            ConfigEntryNormalizer.RepairDuplicateDataKeys(
                normalizedGames,
                _logger,
                " while loading.");

            foreach (var game in normalizedGames)
            {
                result[game.DataKey] = game;
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
                    return new AppConfig
                    {
                        Games = new List<GameConfig>(),
                        ProcessMonitorType = ProcessMonitorType.ETW
                    };
                }

                var yaml = File.ReadAllText(_configFilePath);

                var storedConfig = Deserializer.Deserialize<StoredAppConfig?>(yaml);
                return ToAppConfig(storedConfig);
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
                .Select(config => ConfigEntryNormalizer.NormalizeForSave(config, _logger))
                .ToList();

            ConfigEntryNormalizer.RepairDuplicateDataKeys(normalizedGames, _logger, " while saving.");

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

            var yaml = Serializer.Serialize(ToStoredAppConfig(appConfig));
            File.WriteAllText(_configFilePath, yaml);
        }

        private static string ResolveDefaultPath() => AppDataPath.GetConfigPath();

        private static AppConfig ToAppConfig(StoredAppConfig? storedConfig)
        {
            if (storedConfig is null)
            {
                return new AppConfig
                {
                    Games = new List<GameConfig>(),
                    ProcessMonitorType = ProcessMonitorType.ETW
                };
            }

            return new AppConfig
            {
                ProcessMonitorType = storedConfig.Monitor ?? ProcessMonitorType.ETW,
                AutoStartInteractiveMonitor = storedConfig.Startup?.AutoStartMonitor ?? false,
                LaunchOnSystemStartup = storedConfig.Startup?.LaunchOnStartup ?? false,
                Games = storedConfig.Games?
                    .Select(ToGameConfig)
                    .ToList() ?? new List<GameConfig>()
            };
        }

        private static GameConfig ToGameConfig(StoredGameConfig storedGameConfig) => new()
        {
            DataKey = storedGameConfig.DataKey ?? string.Empty,
            Executable = storedGameConfig.Executable,
            DisplayName = storedGameConfig.DisplayName,
            IsEnabled = storedGameConfig.Enabled ?? true,
            HdrEnabled = storedGameConfig.Hdr ?? false
        };

        private static StoredAppConfig ToStoredAppConfig(AppConfig appConfig) => new()
        {
            Monitor = appConfig.ProcessMonitorType ?? ProcessMonitorType.ETW,
            Startup = new StoredStartupConfig
            {
                AutoStartMonitor = appConfig.AutoStartInteractiveMonitor,
                LaunchOnStartup = appConfig.LaunchOnSystemStartup
            },
            Games = appConfig.Games?
                .Select(ToStoredGameConfig)
                .OrderBy(game => game.DataKey, StringComparer.OrdinalIgnoreCase)
                .ToList() ?? new List<StoredGameConfig>()
        };

        private static StoredGameConfig ToStoredGameConfig(GameConfig gameConfig) => new()
        {
            DataKey = gameConfig.DataKey,
            Executable = gameConfig.Executable,
            DisplayName = gameConfig.DisplayName,
            Enabled = gameConfig.IsEnabled,
            Hdr = gameConfig.HdrEnabled
        };

        private sealed class StoredAppConfig
        {
            public ProcessMonitorType? Monitor { get; set; }

            public StoredStartupConfig? Startup { get; set; }

            public List<StoredGameConfig>? Games { get; set; }
        }

        private sealed class StoredStartupConfig
        {
            public bool AutoStartMonitor { get; set; }

            public bool LaunchOnStartup { get; set; }
        }

        private sealed class StoredGameConfig
        {
            public string? DataKey { get; set; }

            public string? Executable { get; set; }

            public string? DisplayName { get; set; }

            public bool? Enabled { get; set; }

            public bool? Hdr { get; set; }
        }
    }
}
