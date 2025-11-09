using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using GameHelper.Core.Abstractions;
using GameHelper.Core.Models;

namespace GameHelper.Infrastructure.Providers
{
    /// <summary>
    /// JSON-based config provider stored at %AppData%/GameHelper/config.json
    /// Compatible with legacy format where games is an array of strings.
    /// </summary>
    public sealed class JsonConfigProvider : IConfigProvider, IConfigPathProvider
    {
        private readonly string _configFilePath;

        public JsonConfigProvider()
            : this(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                                 "GameHelper", "config.json"))
        {
        }

        // For tests
        public JsonConfigProvider(string configFilePath)
        {
            _configFilePath = configFilePath;
        }

        public string ConfigPath => _configFilePath;

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

                var json = File.ReadAllText(_configFilePath);
                var root = JsonSerializer.Deserialize<Dictionary<string, object>>(json);
                var comparer = StringComparer.OrdinalIgnoreCase;
                var result = new Dictionary<string, GameConfig>(comparer);

                if (root != null && root.TryGetValue("games", out var gamesNode) && gamesNode != null)
                {
                    // Try new format first: array of GameConfig
                    try
                    {
                        var gameConfigs = JsonSerializer.Deserialize<GameConfig[]>(gamesNode.ToString() ?? string.Empty);
                        if (gameConfigs != null)
                        {
                            foreach (var g in gameConfigs)
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
                            return result;
                        }
                    }
                    catch
                    {
                        // fallthrough to legacy format
                    }

                    // Legacy format: array of strings
                    try
                    {
                        var names = JsonSerializer.Deserialize<string[]>(gamesNode.ToString() ?? string.Empty);
                        if (names != null)
                        {
                            foreach (var n in names)
                            {
                                if (!string.IsNullOrWhiteSpace(n))
                                {
                                    result[n] = new GameConfig { Name = n, Alias = null, IsEnabled = true, HDREnabled = false };
                                }
                            }
                        }
                    }
                    catch
                    {
                        // ignore malformed legacy content; return empty
                    }
                }

                return result;
            }
            catch
            {
                // On any error, return empty to avoid crashing caller
                return new Dictionary<string, GameConfig>(StringComparer.OrdinalIgnoreCase);
            }
        }

        public void Save(IReadOnlyDictionary<string, GameConfig> configs)
        {
            var dir = Path.GetDirectoryName(_configFilePath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            var payload = new Dictionary<string, object>
            {
                ["games"] = configs.Values
                    .Where(v => !string.IsNullOrWhiteSpace(v.Name))
                    .OrderBy(v => v.Name, StringComparer.OrdinalIgnoreCase)
                    .Select(v => new GameConfig
                    {
                        Name = v.Name,
                        Alias = string.IsNullOrWhiteSpace(v.Alias) ? null : v.Alias,
                        IsEnabled = v.IsEnabled,
                        HDREnabled = v.HDREnabled
                    })
                    .ToArray()
            };

            var json = JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(_configFilePath, json);
        }
    }
}
