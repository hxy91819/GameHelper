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

            ConfigEntryNormalizer.RepairDuplicateIdentities(
                normalizedGames,
                _logger,
                " while loading.");

            foreach (var game in normalizedGames)
            {
                result[game.EntryId] = game;
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

                try
                {
                    var appConfig = Deserializer.Deserialize<AppConfig?>(yaml);
                    if (appConfig != null)
                    {
                        appConfig.ProcessMonitorType ??= ProcessMonitorType.ETW;
                        return appConfig;
                    }
                }
                catch
                {
                    // fall through to legacy root parsing
                }

                var legacyRoot = Deserializer.Deserialize<LegacyRoot?>(yaml);
                return new AppConfig
                {
                    Games = legacyRoot?.Games ?? new List<GameConfig>(),
                    ProcessMonitorType = ProcessMonitorType.ETW
                };
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

            ConfigEntryNormalizer.RepairDuplicateIdentities(normalizedGames, _logger, " while saving.");

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

        private sealed class LegacyRoot
        {
            public List<GameConfig>? Games { get; set; }
        }
    }
}
