using GameHelper.Core.Abstractions;
using GameHelper.Core.Models;
using GameHelper.Core.Utilities;

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
        return configs.Values
            .OrderBy(v => v.DisplayName ?? v.DataKey, StringComparer.OrdinalIgnoreCase)
            .Select(ToEntry)
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
        var dataKey = ConfigIdentity.EnsureUniqueDataKey(executableName, configs.Values.Select(v => v.DataKey));
        var entryId = ConfigIdentity.EnsureEntryId(null);

        var config = new GameConfig
        {
            EntryId = entryId,
            DataKey = dataKey,
            ExecutableName = executableName,
            ExecutablePath = request.ExecutablePath,
            DisplayName = request.DisplayName,
            IsEnabled = request.IsEnabled,
            HDREnabled = request.HdrEnabled
        };

        configs[entryId] = config;
        _configProvider.Save(configs);
        _cachedConfigs = configs;
        return ToEntry(config);
    }

    public GameEntry Update(string dataKey, GameEntryUpsertRequest request)
    {
        if (string.IsNullOrWhiteSpace(dataKey))
        {
            throw new ArgumentException("Data key is required.", nameof(dataKey));
        }

        var configs = new Dictionary<string, GameConfig>(LoadConfigs(), StringComparer.OrdinalIgnoreCase);
        var existingPair = configs.FirstOrDefault(kv =>
            string.Equals(kv.Value.DataKey, dataKey, StringComparison.OrdinalIgnoreCase));

        if (existingPair.Value is null)
        {
            throw new KeyNotFoundException($"Game '{dataKey}' not found.");
        }

        // Clone the existing config to avoid modifying the cache directly
        // The dictionary 'configs' is a shallow copy of the cached dictionary, so values are references.
        var original = existingPair.Value;
        var updated = new GameConfig
        {
            EntryId = string.IsNullOrWhiteSpace(original.EntryId) ? existingPair.Key : original.EntryId,
            DataKey = original.DataKey,
            ExecutableName = NormalizeExecutableName(request.ExecutableName) ?? original.ExecutableName,
            ExecutablePath = request.ExecutablePath ?? original.ExecutablePath,
            DisplayName = request.DisplayName ?? original.DisplayName,
            IsEnabled = request.IsEnabled,
            HDREnabled = request.HdrEnabled
        };

        configs[existingPair.Key] = updated;
        _configProvider.Save(configs);
        _cachedConfigs = configs;
        return ToEntry(updated);
    }

    public bool Delete(string dataKey)
    {
        if (string.IsNullOrWhiteSpace(dataKey))
        {
            return false;
        }

        var configs = new Dictionary<string, GameConfig>(LoadConfigs(), StringComparer.OrdinalIgnoreCase);
        var existingPair = configs.FirstOrDefault(kv =>
            string.Equals(kv.Value.DataKey, dataKey, StringComparison.OrdinalIgnoreCase));

        if (existingPair.Value is null)
        {
            return false;
        }

        configs.Remove(existingPair.Key);
        _configProvider.Save(configs);
        _cachedConfigs = configs;
        return true;
    }

    private static GameEntry ToEntry(GameConfig config) => new()
    {
        DataKey = config.DataKey,
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
