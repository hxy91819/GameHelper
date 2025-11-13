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
                                var normalized = NormalizeLoadedConfig(g);
                                var key = DetermineDictionaryKey(normalized);
                                if (string.IsNullOrWhiteSpace(key)) continue;
                                result[key] = normalized;
                            }
                            return result;
                        }
                    }
                    catch (InvalidDataException)
                    {
                        throw;
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
                                    var trimmed = n.Trim();
                                    if (trimmed.Length == 0) continue;
                                    result[trimmed] = new GameConfig
                                    {
                                        DataKey = trimmed,
                                        ExecutableName = trimmed,
                                        DisplayName = null,
                                        IsEnabled = true,
                                        HDREnabled = false
                                    };
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
            catch (InvalidDataException)
            {
                throw;
            }
            catch
            {
                // On other errors, return empty to avoid crashing caller
                return new Dictionary<string, GameConfig>(StringComparer.OrdinalIgnoreCase);
            }
        }

        public void Save(IReadOnlyDictionary<string, GameConfig> configs)
        {
            var dir = Path.GetDirectoryName(_configFilePath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            var payload = new Dictionary<string, object>
            {
                ["games"] = configs
                    .Select(kv => NormalizeForSave(kv.Key, kv.Value))
                    .Where(cfg => cfg.HasValue)
                    .Select(cfg => cfg!.Value)
                    .OrderBy(cfg => cfg.ExecutableKey, StringComparer.OrdinalIgnoreCase)
                    .Select(cfg => cfg.Game)
                    .ToArray()
            };

            var json = JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(_configFilePath, json);
        }

        private static GameConfig NormalizeLoadedConfig(GameConfig source)
        {
            var dataKey = (source.DataKey ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(dataKey))
            {
                throw new InvalidDataException("配置项缺少必填字段 DataKey，无法完成加载。");
            }

            var executableName = (source.ExecutableName ?? source.Name ?? string.Empty).Trim();
            var executablePath = (source.ExecutablePath ?? string.Empty).Trim();
            var displayName = (source.DisplayName ?? source.Alias ?? string.Empty).Trim();

            return new GameConfig
            {
                DataKey = dataKey,
                ExecutableName = executableName.Length == 0 ? null : executableName,
                ExecutablePath = executablePath.Length == 0 ? null : executablePath,
                DisplayName = displayName.Length == 0 ? null : displayName,
                IsEnabled = source.IsEnabled,
                HDREnabled = source.HDREnabled
            };
        }

        private static (string ExecutableKey, GameConfig Game)? NormalizeForSave(string key, GameConfig source)
        {
            var executableName = (source.ExecutableName ?? source.Name ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(executableName))
            {
                executableName = (key ?? string.Empty).Trim();
            }

            var dataKey = (source.DataKey ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(dataKey))
            {
                throw new InvalidDataException($"无法保存缺少 DataKey 的配置项（字典键：{key}）。");
            }

            var executablePath = (source.ExecutablePath ?? string.Empty).Trim();
            var displayName = (source.DisplayName ?? source.Alias ?? string.Empty).Trim();

            var normalized = new GameConfig
            {
                DataKey = dataKey,
                ExecutableName = executableName.Length == 0 ? null : executableName,
                ExecutablePath = executablePath.Length == 0 ? null : executablePath,
                DisplayName = displayName.Length == 0 ? null : displayName,
                IsEnabled = source.IsEnabled,
                HDREnabled = source.HDREnabled
            };

            return (executableName.Length == 0 ? dataKey : executableName, normalized);
        }

        private static string? DetermineDictionaryKey(GameConfig config)
        {
            if (!string.IsNullOrWhiteSpace(config.ExecutableName))
            {
                return config.ExecutableName;
            }

            return config.DataKey;
        }
    }
}
