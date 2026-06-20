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

    public GameEntry? FindExistingForAdd(string executableName, string? executablePath)
    {
        var normalizedExecutableName = NormalizeExecutableName(executableName);
        if (string.IsNullOrWhiteSpace(normalizedExecutableName))
        {
            return null;
        }

        var configs = _configProvider.Load();
        var existing = ConfigEntryMatcher.FindExistingForAdd(configs.Values, normalizedExecutableName, executablePath);
        return existing is null ? null : ToEntry(existing);
    }

    public string SuggestDataKey(string executableIdentity, string? productName = null)
    {
        var existingKeys = _configProvider.Load().Values.Select(config => config.DataKey);
        return DataKeyGenerator.GenerateUniqueDataKey(executableIdentity, productName, existingKeys);
    }

    public bool IsDataKeyAvailable(string dataKey, string? currentDataKey = null)
    {
        if (string.IsNullOrWhiteSpace(dataKey))
        {
            return false;
        }

        var requested = dataKey.Trim();
        return _configProvider.Load().Values.All(config =>
            !string.Equals(config.DataKey, requested, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(config.DataKey, currentDataKey, StringComparison.OrdinalIgnoreCase));
    }

    public GameEntry Add(GameEntryUpsertRequest request)
    {
        var executableName = NormalizeExecutableName(request.ExecutableName);
        if (string.IsNullOrWhiteSpace(executableName))
        {
            throw new ArgumentException("ExecutableName is required.", nameof(request));
        }

        var configs = CreateWorkingMapByDataKey(_configProvider.Load());
        var requestedDataKey = string.IsNullOrWhiteSpace(request.DataKey) ? executableName : request.DataKey;
        var dataKey = ConfigIdentity.EnsureUniqueDataKey(requestedDataKey, configs.Values.Select(v => v.DataKey));

        var config = new GameConfig
        {
            DataKey = dataKey,
            ExecutableName = executableName,
            ExecutablePath = request.ExecutablePath,
            DisplayName = request.DisplayName,
            IsEnabled = request.IsEnabled,
            HdrEnabled = request.HdrEnabled
        };

        configs[dataKey] = config;
        _configProvider.Save(configs);
        return ToEntry(config);
    }

    public GameEntry Save(GameEntryUpsertRequest request)
    {
        var executableName = NormalizeExecutableName(request.ExecutableName);
        if (string.IsNullOrWhiteSpace(executableName))
        {
            throw new ArgumentException("ExecutableName is required.", nameof(request));
        }

        var configs = CreateWorkingMapByDataKey(_configProvider.Load());
        var existing = ConfigEntryMatcher.FindExistingForAdd(configs.Values, executableName, request.ExecutablePath);
        var existingDataKey = existing?.DataKey;
        var requestedDataKey = string.IsNullOrWhiteSpace(request.DataKey) ? executableName : request.DataKey;
        var dataKey = ResolveRequestedDataKey(requestedDataKey, configs.Values, existingDataKey);

        var config = existing ?? new GameConfig();
        config.DataKey = dataKey;
        config.ExecutableName = executableName;
        config.ExecutablePath = request.ClearExecutablePath ? null : request.ExecutablePath;
        config.DisplayName = request.ClearDisplayName ? null : request.DisplayName;
        config.IsEnabled = request.IsEnabled;
        config.HdrEnabled = request.HdrEnabled;

        if (!string.IsNullOrWhiteSpace(existingDataKey) &&
            !string.Equals(existingDataKey, dataKey, StringComparison.OrdinalIgnoreCase))
        {
            configs.Remove(existingDataKey);
        }

        configs[dataKey] = config;
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

        var configs = CreateWorkingMapByDataKey(_configProvider.Load());
        var existing = ConfigEntryMatcher.FindExistingForAdd(configs.Values, executableName, executablePath);
        if (existing is not null)
        {
            var previousExecutablePath = existing.ExecutablePath;
            existing.DataKey = EnsureExistingDataKey(existing, configs.Values, request.BaseDataKey ?? executableName);
            existing.ExecutablePath = executablePath;
            existing.ExecutableName = executableName;
            existing.DisplayName = request.DisplayName;
            existing.IsEnabled = request.IsEnabled;

            configs[existing.DataKey] = existing;
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
        var config = new GameConfig
        {
            DataKey = dataKey,
            ExecutablePath = executablePath,
            ExecutableName = executableName,
            DisplayName = request.DisplayName,
            IsEnabled = request.IsEnabled,
            HdrEnabled = false
        };

        configs[dataKey] = config;
        _configProvider.Save(configs);
        return new GameEntryImportResult
        {
            Entry = ToEntry(config),
            WasAdded = true
        };
    }

    public void RepairStorage()
    {
        var configs = CreateWorkingMapByDataKey(_configProvider.Load());
        _configProvider.Save(configs);
    }

    public GameEntry Update(string dataKey, GameEntryUpsertRequest request)
    {
        if (string.IsNullOrWhiteSpace(dataKey))
        {
            throw new ArgumentException("Data key is required.", nameof(dataKey));
        }

        var configs = CreateWorkingMapByDataKey(_configProvider.Load());
        var existing = FindByDataKey(configs.Values, dataKey);
        if (existing is null)
        {
            throw new KeyNotFoundException($"Game '{dataKey}' not found.");
        }

        existing.ExecutableName = NormalizeExecutableName(request.ExecutableName) ?? existing.ExecutableName;
        existing.ExecutablePath = request.ClearExecutablePath ? null : request.ExecutablePath ?? existing.ExecutablePath;
        existing.DisplayName = request.ClearDisplayName ? null : request.DisplayName ?? existing.DisplayName;
        existing.IsEnabled = request.IsEnabled;
        existing.HdrEnabled = request.HdrEnabled;

        configs[existing.DataKey] = existing;
        _configProvider.Save(configs);
        return ToEntry(existing);
    }

    public bool Delete(string dataKey)
    {
        if (string.IsNullOrWhiteSpace(dataKey))
        {
            return false;
        }

        var configs = CreateWorkingMapByDataKey(_configProvider.Load());
        var existing = FindByDataKey(configs.Values, dataKey);
        if (existing is null)
        {
            return false;
        }

        configs.Remove(existing.DataKey);
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
        HdrEnabled = config.HdrEnabled
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

    private static Dictionary<string, GameConfig> CreateWorkingMapByDataKey(IReadOnlyDictionary<string, GameConfig> configs)
    {
        var result = new Dictionary<string, GameConfig>(StringComparer.OrdinalIgnoreCase);
        var usedDataKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var config in configs.Values)
        {
            config.DataKey = ConfigIdentity.EnsureUniqueDataKey(config.DataKey, usedDataKeys);
            result[config.DataKey] = config;
        }

        return result;
    }

    private static GameConfig? FindByDataKey(IEnumerable<GameConfig> configs, string dataKey)
    {
        return configs.FirstOrDefault(config =>
            string.Equals(config.DataKey, dataKey, StringComparison.OrdinalIgnoreCase));
    }

    private static string EnsureExistingDataKey(GameConfig existing, IEnumerable<GameConfig> allConfigs, string baseDataKey)
    {
        if (!string.IsNullOrWhiteSpace(existing.DataKey))
        {
            return existing.DataKey;
        }

        var keys = allConfigs
            .Where(c => !string.Equals(c.DataKey, existing.DataKey, StringComparison.OrdinalIgnoreCase))
            .Select(c => c.DataKey);

        return ConfigIdentity.EnsureUniqueDataKey(baseDataKey, keys);
    }

    private static string ResolveRequestedDataKey(string? requestedDataKey, IEnumerable<GameConfig> allConfigs, string? currentDataKey)
    {
        if (string.IsNullOrWhiteSpace(requestedDataKey))
        {
            throw new ArgumentException("DataKey is required.", nameof(requestedDataKey));
        }

        var dataKey = requestedDataKey.Trim();
        var isUsedByAnotherEntry = allConfigs.Any(config =>
            string.Equals(config.DataKey, dataKey, StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(config.DataKey, currentDataKey, StringComparison.OrdinalIgnoreCase));
        if (isUsedByAnotherEntry)
        {
            throw new InvalidOperationException($"DataKey '{dataKey}' is already used by another game.");
        }

        return dataKey;
    }

}
