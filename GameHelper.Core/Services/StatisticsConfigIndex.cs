using GameHelper.Core.Models;
using GameHelper.Core.Utilities;

namespace GameHelper.Core.Services;

internal sealed class StatisticsConfigIndex
{
    private readonly GameConfigLookup _lookup;

    private StatisticsConfigIndex(GameConfigLookup lookup)
    {
        _lookup = lookup;
    }

    public static StatisticsConfigIndex Build(IReadOnlyDictionary<string, GameConfig> configs)
    {
        var workingMap = new Dictionary<string, GameConfig>(configs, StringComparer.OrdinalIgnoreCase);
        return new StatisticsConfigIndex(GameConfigLookup.Build(workingMap));
    }

    public string? FindDisplayName(string key)
    {
        return _lookup.Resolve(key)?.DisplayName;
    }

    public string ResolveDisplayName(string key)
    {
        var displayName = FindDisplayName(key);
        return !string.IsNullOrWhiteSpace(displayName) ? displayName : key;
    }
}
