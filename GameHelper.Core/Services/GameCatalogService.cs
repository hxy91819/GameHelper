using GameHelper.Core.Abstractions;
using GameHelper.Core.Models;
using GameHelper.Core.Utilities;

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

        var configs = new Dictionary<string, GameConfig>(_configProvider.Load(), StringComparer.OrdinalIgnoreCase);
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
        return ToEntry(config);
    }

    public GameEntry Update(string dataKey, GameEntryUpsertRequest request)
    {
        if (string.IsNullOrWhiteSpace(dataKey))
        {
            throw new ArgumentException("Data key is required.", nameof(dataKey));
        }

        var configs = new Dictionary<string, GameConfig>(_configProvider.Load(), StringComparer.OrdinalIgnoreCase);
        var existingPair = configs.FirstOrDefault(kv =>
            string.Equals(kv.Value.DataKey, dataKey, StringComparison.OrdinalIgnoreCase));

        if (existingPair.Value is null)
        {
            throw new KeyNotFoundException($"Game '{dataKey}' not found.");
        }

        var existing = existingPair.Value;
        existing.EntryId = string.IsNullOrWhiteSpace(existing.EntryId) ? existingPair.Key : existing.EntryId;
        existing.ExecutableName = NormalizeExecutableName(request.ExecutableName) ?? existing.ExecutableName;
        existing.ExecutablePath = request.ExecutablePath ?? existing.ExecutablePath;
        existing.DisplayName = request.DisplayName ?? existing.DisplayName;
        existing.IsEnabled = request.IsEnabled;
        existing.HDREnabled = request.HdrEnabled;

        configs[existingPair.Key] = existing;
        _configProvider.Save(configs);
        return ToEntry(existing);
    }

    public bool Delete(string dataKey)
    {
        if (string.IsNullOrWhiteSpace(dataKey))
        {
            return false;
        }

        var configs = new Dictionary<string, GameConfig>(_configProvider.Load(), StringComparer.OrdinalIgnoreCase);
        var existingPair = configs.FirstOrDefault(kv =>
            string.Equals(kv.Value.DataKey, dataKey, StringComparison.OrdinalIgnoreCase));

        if (existingPair.Value is null)
        {
            return false;
        }

        configs.Remove(existingPair.Key);
        _configProvider.Save(configs);
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
