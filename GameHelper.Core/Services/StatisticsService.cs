using GameHelper.Core.Abstractions;
using GameHelper.Core.Models;
using GameHelper.Core.Utilities;

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
        var configLookup = GameConfigLookup.Build(configs);
        var cutoff = DateTime.Now.AddDays(-14);

        return records
            .Select(record => ToSummary(record, configLookup, cutoff))
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
        var configLookup = GameConfigLookup.Build(configs);
        var cutoff = DateTime.Now.AddDays(-14);
        var match = records.FirstOrDefault(record =>
            string.Equals(record.GameName, dataKeyOrGameName, StringComparison.OrdinalIgnoreCase));

        return match is null ? null : ToSummary(match, configLookup, cutoff);
    }

    private static GameStatsSummary ToSummary(
        GamePlaytimeRecord record,
        GameConfigLookup configLookup,
        DateTime cutoff)
    {
        var gameConfig = configLookup.Resolve(record.GameName);
        var displayName = gameConfig?.DisplayName;

        var orderedSessions = record.Sessions
            .OrderByDescending(item => item.StartTime)
            .ToList();

        // Compute aggregates in a single pass to avoid multiple enumerations
        long totalMinutes = 0;
        long recentMinutes = 0;

        foreach (var session in orderedSessions)
        {
            totalMinutes += session.DurationMinutes;
            if (session.EndTime >= cutoff)
            {
                recentMinutes += session.DurationMinutes;
            }
        }

        return new GameStatsSummary
        {
            GameName = record.GameName,
            DisplayName = displayName,
            TotalMinutes = totalMinutes,
            RecentMinutes = recentMinutes,
            SessionCount = orderedSessions.Count,
            Sessions = orderedSessions
        };
    }

}
