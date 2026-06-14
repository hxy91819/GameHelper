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

    public GameEntryImportResult Import(GameEntryImportRequest request)
    {
        var executableName = NormalizeExecutableName(request.ExecutableName);
        if (string.IsNullOrWhiteSpace(executableName))
        {
            throw new ArgumentException("ExecutableName is required.", nameof(request));
        }

        var executablePath = NormalizeImportPath(request.ExecutablePath);
        if (string.IsNullOrWhiteSpace(executablePath))
        {
            throw new ArgumentException("ExecutablePath is required.", nameof(request));
        }

        var configs = CreateWorkingMapByEntryId(_configProvider.Load());
        var existing = ConfigEntryMatcher.FindExistingForAdd(configs.Values, executableName, executablePath);
        if (existing is not null)
        {
            var previousExecutablePath = existing.ExecutablePath;
            existing.EntryId = ConfigIdentity.EnsureEntryId(existing.EntryId);
            existing.DataKey = EnsureExistingDataKey(existing, configs.Values, request.BaseDataKey ?? executableName);
            existing.ExecutablePath = executablePath;
            existing.ExecutableName = executableName;
            existing.DisplayName = request.DisplayName;
            existing.IsEnabled = request.IsEnabled;

            configs[existing.EntryId] = existing;
            _configProvider.Save(configs);
            return new GameEntryImportResult
            {
                Entry = ToEntry(existing),
                WasAdded = false,
                PreviousExecutablePath = previousExecutablePath
            };
        }

        var dataKey = ConfigIdentity.EnsureUniqueDataKey(
            request.BaseDataKey ?? executableName,
            configs.Values.Select(c => c.DataKey));
        var entryId = ConfigIdentity.EnsureEntryId(null);
        var config = new GameConfig
        {
            EntryId = entryId,
            DataKey = dataKey,
            ExecutablePath = executablePath,
            ExecutableName = executableName,
            DisplayName = request.DisplayName,
            IsEnabled = request.IsEnabled,
            HDREnabled = false
        };

        configs[entryId] = config;
        _configProvider.Save(configs);
        return new GameEntryImportResult
        {
            Entry = ToEntry(config),
            WasAdded = true
        };
    }

    public void RepairStorage()
    {
        var configs = CreateWorkingMapByEntryId(_configProvider.Load());
        _configProvider.Save(configs);
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
        existing.ExecutablePath = request.ClearExecutablePath ? null : request.ExecutablePath ?? existing.ExecutablePath;
        existing.DisplayName = request.ClearDisplayName ? null : request.DisplayName ?? existing.DisplayName;
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

    private static string? NormalizeImportPath(string? executablePath)
    {
        if (string.IsNullOrWhiteSpace(executablePath))
        {
            return null;
        }

        return executablePath.Trim();
    }

    private static Dictionary<string, GameConfig> CreateWorkingMapByEntryId(IReadOnlyDictionary<string, GameConfig> configs)
    {
        return configs.Values.ToDictionary(
            config => ConfigIdentity.EnsureEntryId(config.EntryId),
            config => config,
            StringComparer.OrdinalIgnoreCase);
    }

    private static string EnsureExistingDataKey(GameConfig existing, IEnumerable<GameConfig> allConfigs, string baseDataKey)
    {
        if (!string.IsNullOrWhiteSpace(existing.DataKey))
        {
            return existing.DataKey;
        }

        var keys = allConfigs
            .Where(c => !string.Equals(c.EntryId, existing.EntryId, StringComparison.OrdinalIgnoreCase))
            .Select(c => c.DataKey);

        return ConfigIdentity.EnsureUniqueDataKey(baseDataKey, keys);
    }

}
