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

                var yaml = File.ReadAllText(_configFilePath);

                var storedConfig = Deserializer.Deserialize<StoredAppConfig?>(yaml);
                return ToAppConfig(storedConfig);
            }
            catch (YamlException ex)
            {
                throw CreateInvalidConfigException(ex);
            }
        }

        private AppConfig Normalize(AppConfig appConfig, bool forSave = false)
        {
            var normalizedGames = new List<GameConfig>();
            foreach (var source in appConfig.Games ?? Enumerable.Empty<GameConfig>())
            {
                var normalized = forSave
                    ? ConfigEntryNormalizer.NormalizeForSave(source, _logger)
                    : ConfigEntryNormalizer.NormalizeLoaded(source, MissingDataKeyAction.Skip, _logger);
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

            var yaml = Serializer.Serialize(ToStoredAppConfig(appConfig));
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
            Monitor = appConfig.ProcessMonitorType,
            Startup = new StoredStartupConfig
            {
                AutoStartMonitor = appConfig.AutoStartInteractiveMonitor,
                LaunchOnStartup = appConfig.LaunchOnSystemStartup
            },
            Games = appConfig.Games
                .Select(ToStoredGameConfig)
                .OrderBy(game => game.DataKey, StringComparer.OrdinalIgnoreCase)
                .ToList()
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
