using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using GameHelper.Core.Abstractions;
using GameHelper.Core.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using YamlDotNet.Core;
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
        private readonly ILogger<YamlConfigProvider> _logger;

        public YamlConfigProvider()
            : this(ResolveDefaultPath(), NullLogger<YamlConfigProvider>.Instance)
        {
        }

        public YamlConfigProvider(ILogger<YamlConfigProvider> logger)
            : this(ResolveDefaultPath(), logger)
        {
        }

        // For tests / overrides
        public YamlConfigProvider(string configFilePath)
            : this(configFilePath, NullLogger<YamlConfigProvider>.Instance)
        {
        }

        public YamlConfigProvider(string configFilePath, ILogger<YamlConfigProvider> logger)
        {
            if (string.IsNullOrWhiteSpace(configFilePath))
                throw new ArgumentException("configFilePath required", nameof(configFilePath));

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
            var comparer = StringComparer.OrdinalIgnoreCase;
            var result = new Dictionary<string, GameConfig>(comparer);

            if (appConfig.Games == null || appConfig.Games.Count == 0)
            {
                return result;
            }

            foreach (var source in appConfig.Games)
            {
                if (source is null)
                {
                    continue;
                }

                var normalized = NormalizeLoadedConfig(source);
                var key = DetermineDictionaryKey(normalized);
                if (string.IsNullOrWhiteSpace(key))
                {
                    _logger.LogWarning("跳过配置项 {DataKey}：缺少 ExecutableName，暂无法参与进程匹配。", normalized.DataKey);
                    continue;
                }

                result[key] = normalized;
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
                    return new AppConfig 
                    { 
                        Games = new List<GameConfig>(),
                        ProcessMonitorType = ProcessMonitorType.ETW // Default to ETW
                    };
                }

                var yaml = File.ReadAllText(_configFilePath);
                
                // Try to deserialize as new AppConfig format first
                try
                {
                    var appConfig = Deserializer.Deserialize<AppConfig?>(yaml);
                    if (appConfig != null)
                    {
                        // If ProcessMonitorType is not specified, default to ETW
                        appConfig.ProcessMonitorType ??= ProcessMonitorType.ETW;
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
                    ProcessMonitorType = ProcessMonitorType.ETW // Default to ETW for all configs
                };
            }
            catch (YamlException ex)
            {
                // Wrap in a more specific exception type
                throw new InvalidDataException("Failed to deserialize config file. Please check its format.", ex);
            }
        }

        public void Save(IReadOnlyDictionary<string, GameConfig> configs)
        {
            // Load existing app config to preserve global settings
            var appConfig = LoadAppConfig();
            var normalizedGames = configs
                .Select(kv => NormalizeForSave(kv.Key, kv.Value))
                .ToList();

            appConfig.Games = normalizedGames;
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

        private static string ResolveDefaultPath() => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "GameHelper",
            "config.yml");

        private GameConfig NormalizeLoadedConfig(GameConfig source)
        {
            var dataKey = (source.DataKey ?? string.Empty).Trim();
            var executableName = (source.ExecutableName ?? source.Name ?? string.Empty).Trim();
            var executablePath = (source.ExecutablePath ?? string.Empty).Trim();
            var displayName = (source.DisplayName ?? source.Alias ?? string.Empty).Trim();

            if (string.IsNullOrWhiteSpace(dataKey))
            {
                throw new InvalidDataException("配置项缺少必填字段 DataKey，无法完成加载。");
            }

            if (string.IsNullOrWhiteSpace(executablePath) && !string.IsNullOrWhiteSpace(executableName))
            {
                _logger.LogWarning("配置项 {DataKey} 未提供 ExecutablePath，将仅使用 ExecutableName 进行匹配。", dataKey);
            }

            return new GameConfig
            {
                DataKey = dataKey,
                ExecutableName = string.IsNullOrWhiteSpace(executableName) ? null : executableName,
                ExecutablePath = string.IsNullOrWhiteSpace(executablePath) ? null : executablePath,
                DisplayName = string.IsNullOrWhiteSpace(displayName) ? null : displayName,
                IsEnabled = source.IsEnabled,
                HDREnabled = source.HDREnabled
            };
        }

        private GameConfig NormalizeForSave(string key, GameConfig source)
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
            if (string.IsNullOrWhiteSpace(executablePath) && !string.IsNullOrWhiteSpace(executableName))
            {
                _logger.LogWarning("配置项 {DataKey} 在保存时缺少 ExecutablePath，将维持仅 ExecutableName 状态。", dataKey);
            }

            var displayName = (source.DisplayName ?? source.Alias ?? string.Empty).Trim();

            return new GameConfig
            {
                DataKey = dataKey,
                ExecutableName = string.IsNullOrWhiteSpace(executableName) ? null : executableName,
                ExecutablePath = string.IsNullOrWhiteSpace(executablePath) ? null : executablePath,
                DisplayName = string.IsNullOrWhiteSpace(displayName) ? null : displayName,
                IsEnabled = source.IsEnabled,
                HDREnabled = source.HDREnabled
            };
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
