using GameHelper.Core.Abstractions;
using GameHelper.Core.Models;

namespace GameHelper.Core.Services;

public sealed class GameCatalogService : IGameCatalogService
{
    private readonly IConfigProvider _configProvider;
    // Cache to avoid reading from disk on every request.
    private IReadOnlyDictionary<string, GameConfig>? _cachedConfigs;

    public GameCatalogService(IConfigProvider configProvider)
    {
        _configProvider = configProvider;
    }

    // Loads configurations from the provider only if not already cached.
    private IReadOnlyDictionary<string, GameConfig> LoadConfigs()
    {
        if (_cachedConfigs == null)
        {
            _cachedConfigs = _configProvider.Load();
        }
        return _cachedConfigs;
    }

    public IReadOnlyList<GameEntry> GetAll()
    {
        var configs = LoadConfigs();
        return configs
            .OrderBy(kv => kv.Value.DisplayName ?? kv.Key, StringComparer.OrdinalIgnoreCase)
            .Select(kv => ToEntry(kv.Key, kv.Value))
            .ToList();
    }

    public GameEntry Add(GameEntryUpsertRequest request)
    {
        var executableName = NormalizeExecutableName(request.ExecutableName);
        if (string.IsNullOrWhiteSpace(executableName))
        {
            throw new ArgumentException("ExecutableName is required.", nameof(request));
        }

        var configs = new Dictionary<string, GameConfig>(LoadConfigs(), StringComparer.OrdinalIgnoreCase);
        var dataKey = executableName;

        var config = new GameConfig
        {
            DataKey = dataKey,
            ExecutableName = executableName,
            ExecutablePath = request.ExecutablePath,
            DisplayName = request.DisplayName,
            IsEnabled = request.IsEnabled,
            HDREnabled = request.HdrEnabled
        };

        configs[dataKey] = config;
        _configProvider.Save(configs);
        _cachedConfigs = configs;
        return ToEntry(dataKey, config);
    }

    public GameEntry Update(string dataKey, GameEntryUpsertRequest request)
    {
        if (string.IsNullOrWhiteSpace(dataKey))
        {
            throw new ArgumentException("Data key is required.", nameof(dataKey));
        }

        var configs = new Dictionary<string, GameConfig>(LoadConfigs(), StringComparer.OrdinalIgnoreCase);
        if (!configs.TryGetValue(dataKey, out var existing))
        {
            throw new KeyNotFoundException($"Game '{dataKey}' not found.");
        }

        var updated = new GameConfig
        {
            DataKey = existing.DataKey,
            ExecutableName = NormalizeExecutableName(request.ExecutableName) ?? existing.ExecutableName,
            ExecutablePath = request.ExecutablePath ?? existing.ExecutablePath,
            DisplayName = request.DisplayName ?? existing.DisplayName,
            IsEnabled = request.IsEnabled,
            HDREnabled = request.HdrEnabled
        };

        configs[dataKey] = updated;
        _configProvider.Save(configs);
        _cachedConfigs = configs;
        return ToEntry(dataKey, updated);
    }

    public bool Delete(string dataKey)
    {
        if (string.IsNullOrWhiteSpace(dataKey))
        {
            return false;
        }

        var configs = new Dictionary<string, GameConfig>(LoadConfigs(), StringComparer.OrdinalIgnoreCase);
        if (!configs.Remove(dataKey))
        {
            return false;
        }

        _configProvider.Save(configs);
        _cachedConfigs = configs;
        return true;
    }

    private static GameEntry ToEntry(string key, GameConfig config) => new()
    {
        DataKey = key,
        ExecutableName = config.ExecutableName,
        ExecutablePath = config.ExecutablePath,
        DisplayName = config.DisplayName,
        IsEnabled = config.IsEnabled,
        HdrEnabled = config.HDREnabled
    };

    private static string? NormalizeExecutableName(string? executableName)
    {
        if (string.IsNullOrWhiteSpace(executableName))
        {
            return null;
        }

        return executableName.Trim();
    }
}
