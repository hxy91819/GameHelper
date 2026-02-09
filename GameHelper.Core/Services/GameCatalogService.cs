using GameHelper.Core.Abstractions;
using GameHelper.Core.Models;

namespace GameHelper.Core.Services;

public sealed class GameCatalogService : IGameCatalogService
{
    private readonly IConfigProvider _configProvider;

    public GameCatalogService(IConfigProvider configProvider)
    {
        _configProvider = configProvider;
    }

    public IReadOnlyList<GameEntry> GetAll()
    {
        var configs = _configProvider.Load();
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

        var configs = new Dictionary<string, GameConfig>(_configProvider.Load(), StringComparer.OrdinalIgnoreCase);
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
        return ToEntry(dataKey, config);
    }

    public GameEntry Update(string dataKey, GameEntryUpsertRequest request)
    {
        if (string.IsNullOrWhiteSpace(dataKey))
        {
            throw new ArgumentException("Data key is required.", nameof(dataKey));
        }

        var configs = new Dictionary<string, GameConfig>(_configProvider.Load(), StringComparer.OrdinalIgnoreCase);
        if (!configs.TryGetValue(dataKey, out var existing))
        {
            throw new KeyNotFoundException($"Game '{dataKey}' not found.");
        }

        existing.ExecutableName = NormalizeExecutableName(request.ExecutableName) ?? existing.ExecutableName;
        existing.ExecutablePath = request.ExecutablePath ?? existing.ExecutablePath;
        existing.DisplayName = request.DisplayName ?? existing.DisplayName;
        existing.IsEnabled = request.IsEnabled;
        existing.HDREnabled = request.HdrEnabled;

        configs[dataKey] = existing;
        _configProvider.Save(configs);
        return ToEntry(dataKey, existing);
    }

    public bool Delete(string dataKey)
    {
        if (string.IsNullOrWhiteSpace(dataKey))
        {
            return false;
        }

        var configs = new Dictionary<string, GameConfig>(_configProvider.Load(), StringComparer.OrdinalIgnoreCase);
        if (!configs.Remove(dataKey))
        {
            return false;
        }

        _configProvider.Save(configs);
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
