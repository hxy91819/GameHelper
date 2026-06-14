using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using GameHelper.Core.Abstractions;
using GameHelper.Core.Models;
using GameHelper.Core.Utilities;

namespace GameHelper.Infrastructure.Providers
{
    /// <summary>
    /// JSON-based config provider stored at %AppData%/GameHelper/config.json.
    /// Compatible with legacy format where games is an array of strings.
    /// </summary>
    public sealed class JsonConfigProvider : IConfigProvider, IConfigPathProvider
    {
        private readonly string _configFilePath;

        public JsonConfigProvider()
            : this(Path.Combine(AppDataPath.GetGameHelperDirectory(), "config.json"))
        {
        }

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
                if (!string.IsNullOrEmpty(dir))
                {
                    Directory.CreateDirectory(dir);
                }

                if (!File.Exists(_configFilePath))
                {
                    return new Dictionary<string, GameConfig>(StringComparer.OrdinalIgnoreCase);
                }

                var json = File.ReadAllText(_configFilePath);
                var root = JsonSerializer.Deserialize<Dictionary<string, object>>(json);
                if (root is null || !root.TryGetValue("games", out var gamesNode) || gamesNode is null)
                {
                    return new Dictionary<string, GameConfig>(StringComparer.OrdinalIgnoreCase);
                }

                var configs = TryLoadStructuredConfig(gamesNode) ?? TryLoadLegacyNames(gamesNode);
                if (configs.Count == 0)
                {
                    return new Dictionary<string, GameConfig>(StringComparer.OrdinalIgnoreCase);
                }

                ConfigEntryNormalizer.RepairDuplicateIdentities(configs);

                return configs.ToDictionary(
                    cfg => cfg.EntryId,
                    cfg => cfg,
                    StringComparer.OrdinalIgnoreCase);
            }
            catch (InvalidDataException)
            {
                throw;
            }
            catch
            {
                return new Dictionary<string, GameConfig>(StringComparer.OrdinalIgnoreCase);
            }
        }

        public void Save(IReadOnlyDictionary<string, GameConfig> configs)
        {
            var dir = Path.GetDirectoryName(_configFilePath);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }

            var normalized = configs.Values
                .Select(config => ConfigEntryNormalizer.NormalizeForSave(config))
                .ToList();

            ConfigEntryNormalizer.RepairDuplicateIdentities(normalized);

            var payload = new Dictionary<string, object>
            {
                ["games"] = normalized
                    .OrderBy(cfg => cfg.DataKey, StringComparer.OrdinalIgnoreCase)
                    .ToArray()
            };

            var serialized = JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(_configFilePath, serialized);
        }

        private static List<GameConfig>? TryLoadStructuredConfig(object gamesNode)
        {
            try
            {
                var gameConfigs = JsonSerializer.Deserialize<GameConfig[]>(gamesNode.ToString() ?? string.Empty);
                if (gameConfigs is null)
                {
                    return null;
                }

                var result = new List<GameConfig>();
                foreach (var gameConfig in gameConfigs)
                {
                    var normalized = ConfigEntryNormalizer.NormalizeLoaded(gameConfig, MissingDataKeyAction.Throw);
                    if (normalized is not null)
                    {
                        result.Add(normalized);
                    }
                }

                return result;
            }
            catch (InvalidDataException)
            {
                throw;
            }
            catch
            {
                return null;
            }
        }

        private static List<GameConfig> TryLoadLegacyNames(object gamesNode)
        {
            var result = new List<GameConfig>();
            try
            {
                var names = JsonSerializer.Deserialize<string[]>(gamesNode.ToString() ?? string.Empty);
                if (names is null)
                {
                    return result;
                }

                foreach (var name in names)
                {
                    if (string.IsNullOrWhiteSpace(name))
                    {
                        continue;
                    }

                    var trimmed = name.Trim();
                    result.Add(new GameConfig
                    {
                        EntryId = Guid.NewGuid().ToString("N"),
                        DataKey = trimmed,
                        ExecutableName = trimmed,
                        IsEnabled = true,
                        HDREnabled = false
                    });
                }
            }
            catch
            {
                // ignore malformed legacy content
            }

            return result;
        }

    }
}
