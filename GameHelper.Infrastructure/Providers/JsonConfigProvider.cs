using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using GameHelper.Core.Abstractions;
using GameHelper.Core.Models;
using GameHelper.Core.Utilities;
using System.Text;

namespace GameHelper.Infrastructure.Providers
{
    /// <summary>
    /// JSON-based config provider stored at %AppData%/GameHelper/config.json.
    /// </summary>
    public sealed class JsonConfigProvider : IGameConfiguration, IConfigPathProvider
    {
        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            PropertyNameCaseInsensitive = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = true
        };

        private readonly object _gate = new();
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

        public AppConfig Read()
        {
            lock (_gate)
            {
                return ReadCore();
            }
        }

        public AppConfig Change(Action<AppConfig> change)
        {
            ArgumentNullException.ThrowIfNull(change);

            lock (_gate)
            {
                var appConfig = ReadCore();
                change(appConfig);
                var normalized = (appConfig.Games ?? new List<GameConfig>())
                    .Select(config => ConfigEntryNormalizer.NormalizeForSave(config))
                    .ToList();

                ConfigEntryNormalizer.RepairDuplicateDataKeys(normalized);
                appConfig.Games = normalized;
                WriteCore(appConfig);
                return ReadCore();
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
                    return new AppConfig { Games = new List<GameConfig>() };
                }

                var json = File.ReadAllText(_configFilePath, Encoding.UTF8);
                using var document = JsonDocument.Parse(json);
                var root = document.RootElement;
                var storedGames = TryGetProperty(root, "games", out var gamesNode)
                    ? JsonSerializer.Deserialize<List<JsonGameConfigDocument>>(gamesNode.GetRawText(), JsonOptions) ?? new()
                    : new List<JsonGameConfigDocument>();
                var configs = NormalizeLoadedGames(storedGames.Select(game => game.ToGameConfig()));
                ConfigEntryNormalizer.RepairDuplicateDataKeys(configs);
                return new AppConfig
                {
                    Games = configs,
                    ProcessMonitorType = ReadProcessMonitorType(root),
                    AutoStartInteractiveMonitor = ReadBoolean(root, "autoStartInteractiveMonitor"),
                    LaunchOnSystemStartup = ReadBoolean(root, "launchOnSystemStartup"),
                    CheatEngine = ReadCheatEngineSettings(root),
                    BreakReminder = TryGetProperty(root, "breakReminder", out var reminder) && reminder.ValueKind == JsonValueKind.Object
                        ? JsonSerializer.Deserialize<BreakReminderSettings>(reminder.GetRawText(), JsonOptions)
                        : null
                };
            }
            catch (InvalidDataException)
            {
                throw;
            }
            catch
            {
                return new AppConfig { Games = new List<GameConfig>() };
            }
        }

        private void WriteCore(AppConfig appConfig)
        {
            appConfig.BreakReminder?.Validate();
            var dir = Path.GetDirectoryName(_configFilePath);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }

            var payload = new AppConfig
            {
                Games = (appConfig.Games ?? new List<GameConfig>())
                    .OrderBy(cfg => cfg.DataKey, StringComparer.OrdinalIgnoreCase)
                    .ToList(),
                ProcessMonitorType = appConfig.ProcessMonitorType,
                AutoStartInteractiveMonitor = appConfig.AutoStartInteractiveMonitor,
                LaunchOnSystemStartup = appConfig.LaunchOnSystemStartup,
                CheatEngine = appConfig.CheatEngine,
                BreakReminder = appConfig.BreakReminder
            };

            var serialized = JsonSerializer.Serialize(payload, JsonOptions);
            var tempPath = _configFilePath + ".tmp";
            try
            {
                File.WriteAllText(tempPath, serialized);
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

        private static List<GameConfig> NormalizeLoadedGames(IEnumerable<GameConfig>? gameConfigs)
        {
            var result = new List<GameConfig>();
            foreach (var gameConfig in gameConfigs ?? Array.Empty<GameConfig>())
            {
                if (gameConfig is null)
                {
                    continue;
                }

                var normalized = ConfigEntryNormalizer.NormalizeLoaded(gameConfig, MissingDataKeyAction.Throw);
                if (normalized is not null)
                {
                    result.Add(normalized);
                }
            }

            return result;
        }

        private static bool TryGetProperty(JsonElement root, string name, out JsonElement value)
        {
            foreach (var property in root.EnumerateObject())
            {
                if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    value = property.Value;
                    return true;
                }
            }

            value = default;
            return false;
        }

        private static bool ReadBoolean(JsonElement root, string name) =>
            TryGetProperty(root, name, out var value) &&
            value.ValueKind is JsonValueKind.True or JsonValueKind.False &&
            value.GetBoolean();

        private static ProcessMonitorType ReadProcessMonitorType(JsonElement root)
        {
            if (!TryGetProperty(root, "processMonitorType", out var value))
            {
                return ProcessMonitorType.ETW;
            }

            if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var numeric) &&
                Enum.IsDefined(typeof(ProcessMonitorType), numeric))
            {
                return (ProcessMonitorType)numeric;
            }

            return value.ValueKind == JsonValueKind.String &&
                   Enum.TryParse<ProcessMonitorType>(value.GetString(), ignoreCase: true, out var parsed)
                ? parsed
                : ProcessMonitorType.ETW;
        }

        private static CheatEngineSettings? ReadCheatEngineSettings(JsonElement root)
        {
            if (!TryGetProperty(root, "cheatEngine", out var value) || value.ValueKind != JsonValueKind.Object ||
                !TryGetProperty(value, "executable", out var executableNode) || executableNode.ValueKind != JsonValueKind.String)
            {
                return null;
            }

            var executable = executableNode.GetString()?.Trim();
            return string.IsNullOrWhiteSpace(executable)
                ? null
                : new CheatEngineSettings { Executable = executable };
        }

        private sealed class JsonGameConfigDocument
        {
            public string DataKey { get; set; } = string.Empty;

            public string? Executable { get; set; }

            public string? ExecutableName { get; set; }

            public string? ExecutablePath { get; set; }

            public string? DisplayName { get; set; }

            public bool IsEnabled { get; set; } = true;

            public bool HdrEnabled { get; set; }

            public double? SpeedMultiplier { get; set; }

            public string? SpeedAttach { get; set; }
            public GameLaunchOptions? Launch { get; set; }

            public GameConfig ToGameConfig() => new()
            {
                DataKey = DataKey,
                Executable = FirstNonEmpty(Executable, ExecutablePath, ExecutableName),
                DisplayName = DisplayName,
                IsEnabled = IsEnabled,
                HdrEnabled = HdrEnabled,
                SpeedMultiplier = SpeedMultiplier,
                SpeedAttach = SpeedAttach,
                Launch = Launch
            };

            private static string? FirstNonEmpty(params string?[] values) =>
                values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim();
        }

    }
}
