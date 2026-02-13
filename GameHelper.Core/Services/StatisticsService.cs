using GameHelper.Core.Abstractions;
using GameHelper.Core.Models;

namespace GameHelper.Core.Services;

public sealed class StatisticsService : IStatisticsService
{
    private readonly IPlaytimeSnapshotProvider _playtimeSnapshotProvider;
    private readonly IConfigProvider _configProvider;

    public StatisticsService(IPlaytimeSnapshotProvider playtimeSnapshotProvider, IConfigProvider configProvider)
    {
        _playtimeSnapshotProvider = playtimeSnapshotProvider;
        _configProvider = configProvider;
    }

    public IReadOnlyList<GameStatsSummary> GetOverview()
    {
        var records = _playtimeSnapshotProvider.GetPlaytimeRecords();
        if (records.Count == 0)
        {
            return Array.Empty<GameStatsSummary>();
        }

        var configs = new Dictionary<string, GameConfig>(_configProvider.Load(), StringComparer.OrdinalIgnoreCase);
        var configIndexes = BuildConfigIndexes(configs);
        var cutoff = DateTime.Now.AddDays(-14);

        return records
            .Select(record => ToSummary(record, configIndexes, cutoff))
            .OrderByDescending(item => item.RecentMinutes)
            .ThenByDescending(item => item.TotalMinutes)
            .ThenBy(item => item.DisplayName ?? item.GameName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public GameStatsSummary? GetDetails(string dataKeyOrGameName)
    {
        if (string.IsNullOrWhiteSpace(dataKeyOrGameName))
        {
            return null;
        }

        var records = _playtimeSnapshotProvider.GetPlaytimeRecords();
        if (records.Count == 0)
        {
            return null;
        }

        var configs = new Dictionary<string, GameConfig>(_configProvider.Load(), StringComparer.OrdinalIgnoreCase);
        var configIndexes = BuildConfigIndexes(configs);
        var cutoff = DateTime.Now.AddDays(-14);
        var match = records.FirstOrDefault(record =>
            string.Equals(record.GameName, dataKeyOrGameName, StringComparison.OrdinalIgnoreCase));

        return match is null ? null : ToSummary(match, configIndexes, cutoff);
    }

    private static GameStatsSummary ToSummary(
        GamePlaytimeRecord record,
        ConfigIndexes configIndexes,
        DateTime cutoff)
    {
        var gameConfig = ResolveGameConfig(record.GameName, configIndexes);
        var displayName = gameConfig?.DisplayName;

        var orderedSessions = record.Sessions
            .OrderByDescending(item => item.StartTime)
            .ToList();

        return new GameStatsSummary
        {
            GameName = record.GameName,
            DisplayName = displayName,
            TotalMinutes = record.Sessions.Sum(item => item.DurationMinutes),
            RecentMinutes = record.Sessions.Where(item => item.EndTime >= cutoff).Sum(item => item.DurationMinutes),
            SessionCount = record.Sessions.Count,
            Sessions = orderedSessions
        };
    }

    private static ConfigIndexes BuildConfigIndexes(IReadOnlyDictionary<string, GameConfig> configs)
    {
        var byDataKey = configs.Values
            .Where(c => c is not null && !string.IsNullOrWhiteSpace(c.DataKey))
            .ToDictionary(c => c.DataKey!, c => c, StringComparer.OrdinalIgnoreCase);

        var byExecutableName = configs.Values
            .Where(c => c is not null && !string.IsNullOrWhiteSpace(c.ExecutableName))
            .ToDictionary(c => c.ExecutableName!, c => c, StringComparer.OrdinalIgnoreCase);

        return new ConfigIndexes(configs, byDataKey, byExecutableName);
    }

    private static GameConfig? ResolveGameConfig(string key, ConfigIndexes indexes)
    {
        if (indexes.ByDataKey.TryGetValue(key, out var byDataKeyConfig))
        {
            return byDataKeyConfig;
        }

        if (indexes.ByExecutableName.TryGetValue(key, out var byExecutableNameConfig))
        {
            return byExecutableNameConfig;
        }

        return indexes.ByMapKey.TryGetValue(key, out var byMapKeyConfig) ? byMapKeyConfig : null;
    }

    private sealed record ConfigIndexes(
        IReadOnlyDictionary<string, GameConfig> ByMapKey,
        IReadOnlyDictionary<string, GameConfig> ByDataKey,
        IReadOnlyDictionary<string, GameConfig> ByExecutableName);
}
