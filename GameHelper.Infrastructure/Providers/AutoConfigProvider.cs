using System;
using System.Collections.Generic;
using System.IO;
using GameHelper.Core.Abstractions;
using GameHelper.Core.Models;

namespace GameHelper.Infrastructure.Providers
{
    /// <summary>
    /// Chooses YAML if config.yml exists; otherwise falls back to JSON.
    /// Location: %AppData%/GameHelper/
    /// </summary>
    public sealed class AutoConfigProvider : IConfigProvider, IConfigPathProvider
    {
        private readonly IConfigProvider _inner;
        private readonly string _path;

        public AutoConfigProvider()
        {
            string baseDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "GameHelper");
            string yml = Path.Combine(baseDir, "config.yml");
            string json = Path.Combine(baseDir, "config.json");

            if (File.Exists(yml))
            {
                _inner = new YamlConfigProvider(yml);
                _path = yml;
            }
            else
            {
                _inner = new JsonConfigProvider(json);
                _path = json;
            }
        }

        // For tests
        public AutoConfigProvider(string configDirectory)
        {
            string yml = Path.Combine(configDirectory, "config.yml");
            string json = Path.Combine(configDirectory, "config.json");
            if (File.Exists(yml))
            {
                _inner = new YamlConfigProvider(yml);
                _path = yml;
            }
            else
            {
                _inner = new JsonConfigProvider(json);
                _path = json;
            }
        }

        public IReadOnlyDictionary<string, GameConfig> Load() => _inner.Load();
        public void Save(IReadOnlyDictionary<string, GameConfig> configs) => _inner.Save(configs);

        public string ConfigPath => _path;
    }
}
