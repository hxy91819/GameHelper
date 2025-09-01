using System;
using System.Collections.Generic;
using System.IO;
using GameHelper.Core.Abstractions;
using GameHelper.Core.Models;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace GameHelper.Infrastructure.Providers
{
    /// <summary>
    /// YAML-based config provider stored at %AppData%/GameHelper/config.yml
    /// Supports both legacy game-only configuration and new AppConfig format with global settings.
    /// </summary>
    public sealed class YamlConfigProvider : IConfigProvider, IConfigPathProvider, IAppConfigProvider
    {
        private readonly string _configFilePath;

        public YamlConfigProvider()
            : this(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                                 "GameHelper", "config.yml"))
        {
        }

        // For tests
        public YamlConfigProvider(string configFilePath)
        {
            _configFilePath = configFilePath;
        }

        public string ConfigPath => _configFilePath;

        private static IDeserializer Deserializer => new DeserializerBuilder()
            .WithNamingConvention(CamelCaseNamingConvention.Instance)
            .IgnoreUnmatchedProperties()
            .Build();

        private static ISerializer Serializer => new SerializerBuilder()
            .WithNamingConvention(CamelCaseNamingConvention.Instance)
            .ConfigureDefaultValuesHandling(DefaultValuesHandling.OmitNull)
            .Build();

        public IReadOnlyDictionary<string, GameConfig> Load()
        {
            var appConfig = LoadAppConfig();
            var comparer = StringComparer.OrdinalIgnoreCase;
            var result = new Dictionary<string, GameConfig>(comparer);

            if (appConfig.Games != null)
            {
                foreach (var g in appConfig.Games)
                {
                    if (!string.IsNullOrWhiteSpace(g.Name))
                    {
                        result[g.Name] = new GameConfig
                        {
                            Name = g.Name,
                            Alias = g.Alias,
                            IsEnabled = g.IsEnabled,
                            HDREnabled = g.HDREnabled
                        };
                    }
                }
            }

            return result;
        }

        public AppConfig LoadAppConfig()
        {
            try
            {
                var dir = Path.GetDirectoryName(_configFilePath);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

                if (!File.Exists(_configFilePath))
                {
                    return new AppConfig { Games = new List<GameConfig>() };
                }

                var yaml = File.ReadAllText(_configFilePath);
                
                // Try to deserialize as new AppConfig format first
                try
                {
                    var appConfig = Deserializer.Deserialize<AppConfig?>(yaml);
                    if (appConfig != null)
                    {
                        return appConfig;
                    }
                }
                catch
                {
                    // Fall back to legacy format
                }

                // Fall back to legacy Root format for backward compatibility
                var root = Deserializer.Deserialize<Root?>(yaml);
                return new AppConfig
                {
                    Games = root?.Games ?? new List<GameConfig>(),
                    ProcessMonitorType = null // Default to WMI for legacy configs
                };
            }
            catch
            {
                return new AppConfig { Games = new List<GameConfig>() };
            }
        }

        public void Save(IReadOnlyDictionary<string, GameConfig> configs)
        {
            // Load existing app config to preserve global settings
            var appConfig = LoadAppConfig();
            appConfig.Games = new List<GameConfig>(configs.Values);
            SaveAppConfig(appConfig);
        }

        public void SaveAppConfig(AppConfig appConfig)
        {
            var dir = Path.GetDirectoryName(_configFilePath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            var yaml = Serializer.Serialize(appConfig);
            File.WriteAllText(_configFilePath, yaml);
        }

        private sealed class Root
        {
            public List<GameConfig>? Games { get; set; }
        }
    }
}
