using GameHelper.Core.Models;
using GameHelper.Core.Utilities;
using Microsoft.Extensions.Logging;
using System.IO;

namespace GameHelper.Core.Services;

internal sealed class AutomationConfigIndex
{
    private AutomationConfigIndex(
        IReadOnlyDictionary<string, GameConfig> all,
        IReadOnlyDictionary<string, GameConfig> byDataKey,
        IReadOnlyDictionary<string, GameConfig> byPath,
        IReadOnlyDictionary<string, GameConfig[]> byExactName,
        NameConfigEntry[] byName)
    {
        All = all;
        ByDataKey = byDataKey;
        ByPath = byPath;
        ByExactName = byExactName;
        ByName = byName;
    }

    public static AutomationConfigIndex Empty { get; } = new(
        new Dictionary<string, GameConfig>(StringComparer.OrdinalIgnoreCase),
        new Dictionary<string, GameConfig>(StringComparer.OrdinalIgnoreCase),
        new Dictionary<string, GameConfig>(StringComparer.OrdinalIgnoreCase),
        new Dictionary<string, GameConfig[]>(StringComparer.OrdinalIgnoreCase),
        Array.Empty<NameConfigEntry>());

    public IReadOnlyDictionary<string, GameConfig> All { get; }

    public IReadOnlyDictionary<string, GameConfig> ByDataKey { get; }

    public IReadOnlyDictionary<string, GameConfig> ByPath { get; }

    public IReadOnlyDictionary<string, GameConfig[]> ByExactName { get; }

    public NameConfigEntry[] ByName { get; }

    public static AutomationConfigIndex Build(IReadOnlyDictionary<string, GameConfig> configs, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(configs);
        ArgumentNullException.ThrowIfNull(logger);

        var dataKeyMap = new Dictionary<string, GameConfig>(StringComparer.OrdinalIgnoreCase);
        var pathMap = new Dictionary<string, GameConfig>(StringComparer.OrdinalIgnoreCase);
        var exactNameMap = new Dictionary<string, List<GameConfig>>(StringComparer.OrdinalIgnoreCase);
        var nameStats = new Dictionary<string, (int Count, int MissingPathCount)>(StringComparer.OrdinalIgnoreCase);
        var nameEntries = new List<NameConfigEntry>();

        foreach (var config in configs.Values)
        {
            if (config is null || !config.IsEnabled)
            {
                continue;
            }

            if (!string.IsNullOrWhiteSpace(config.DataKey) && !dataKeyMap.TryAdd(config.DataKey, config))
            {
                logger.LogWarning(
                    "Duplicate DataKey detected while building indexes: {DataKey}. Keeping the first entry.",
                    config.DataKey);
            }

            var normalizedPath = PathNormalizer.NormalizePath(config.ExecutablePath);
            if (normalizedPath is not null && !pathMap.TryAdd(normalizedPath, config))
            {
                logger.LogWarning(
                    "Duplicate executable path detected while building indexes: {Path}. Keeping the first entry.",
                    normalizedPath);
            }

            var normalizedName = PathNormalizer.NormalizeName(config.ExecutableName);
            AddExactNameCandidate(exactNameMap, normalizedName, config);
            AddExactNameCandidate(exactNameMap, NormalizeFileNameFromPath(normalizedPath), config);

            if (normalizedName is null)
            {
                continue;
            }

            if (!nameStats.TryGetValue(normalizedName, out var stat))
            {
                stat = (0, 0);
            }

            stat.Count++;
            if (normalizedPath is null)
            {
                stat.MissingPathCount++;
            }

            nameStats[normalizedName] = stat;
            nameEntries.Add(new NameConfigEntry(normalizedName, normalizedName.ToUpperInvariant(), config));
        }

        LogDuplicateExecutableNames(nameStats, logger);

        var exactNameArrays = exactNameMap.ToDictionary(
            kv => kv.Key,
            kv => kv.Value.Distinct().ToArray(),
            StringComparer.OrdinalIgnoreCase);

        return new AutomationConfigIndex(configs, dataKeyMap, pathMap, exactNameArrays, nameEntries.ToArray());
    }

    private static void AddExactNameCandidate(
        IDictionary<string, List<GameConfig>> exactNameMap,
        string? normalizedName,
        GameConfig config)
    {
        if (normalizedName is null)
        {
            return;
        }

        if (!exactNameMap.TryGetValue(normalizedName, out var configs))
        {
            configs = new List<GameConfig>();
            exactNameMap[normalizedName] = configs;
        }

        if (!configs.Contains(config))
        {
            configs.Add(config);
        }
    }

    private static string? NormalizeFileNameFromPath(string? normalizedPath)
    {
        if (string.IsNullOrWhiteSpace(normalizedPath))
        {
            return null;
        }

        try
        {
            return PathNormalizer.NormalizeName(Path.GetFileName(normalizedPath));
        }
        catch
        {
            return null;
        }
    }

    private static void LogDuplicateExecutableNames(
        IReadOnlyDictionary<string, (int Count, int MissingPathCount)> nameStats,
        ILogger logger)
    {
        foreach (var (name, stat) in nameStats.Where(kv => kv.Value.Count > 1))
        {
            if (stat.MissingPathCount > 0)
            {
                logger.LogWarning(
                    "Duplicate executable name detected while building indexes: {Name}. DuplicateCount={Count}, MissingPathCount={MissingPathCount}. Name-only matching may become ambiguous.",
                    name,
                    stat.Count,
                    stat.MissingPathCount);
            }
            else
            {
                logger.LogDebug(
                    "Duplicate executable name detected while building indexes: {Name}. DuplicateCount={Count}. All entries have ExecutablePath, path-first matching remains deterministic.",
                    name,
                    stat.Count);
            }
        }
    }
}
