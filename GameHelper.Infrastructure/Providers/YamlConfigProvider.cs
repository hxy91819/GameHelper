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
    /// </summary>
    public sealed class YamlConfigProvider : IConfigProvider, IConfigPathProvider
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
            try
            {
                var dir = Path.GetDirectoryName(_configFilePath);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

                if (!File.Exists(_configFilePath))
                {
                    return new Dictionary<string, GameConfig>(StringComparer.OrdinalIgnoreCase);
                }

                var yaml = File.ReadAllText(_configFilePath);
                var root = Deserializer.Deserialize<Root?>(yaml);
                var comparer = StringComparer.OrdinalIgnoreCase;
                var result = new Dictionary<string, GameConfig>(comparer);

                if (root?.Games != null)
                {
                    foreach (var g in root.Games)
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
            catch
            {
                return new Dictionary<string, GameConfig>(StringComparer.OrdinalIgnoreCase);
            }
        }

        public void Save(IReadOnlyDictionary<string, GameConfig> configs)
        {
            var dir = Path.GetDirectoryName(_configFilePath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            var payload = new Root
            {
                Games = new List<GameConfig>(configs.Values)
            };

            var yaml = Serializer.Serialize(payload);
            File.WriteAllText(_configFilePath, yaml);
        }

        private sealed class Root
        {
            public List<GameConfig>? Games { get; set; }
        }
    }
}
