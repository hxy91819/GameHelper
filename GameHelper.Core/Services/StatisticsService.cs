using GameHelper.Core.Abstractions;
using GameHelper.Core.Models;

namespace GameHelper.Core.Services;

public sealed class StatisticsService : IStatisticsService
{
    /// <summary>历史记录预览覆盖的最近自然日数量（含今天）。</summary>
    public const int PreviewWindowDays = 14;

    private readonly IPlaytimeSnapshotProvider _playtimeSnapshotProvider;
    private readonly IGameConfiguration _gameConfiguration;

    public StatisticsService(IPlaytimeSnapshotProvider playtimeSnapshotProvider, IGameConfiguration gameConfiguration)
    {
        _playtimeSnapshotProvider = playtimeSnapshotProvider;
        _gameConfiguration = gameConfiguration;
    }

    public IReadOnlyList<GameStatsSummary> GetOverview()
    {
        var configIndex = LoadConfigIndex();
        var cutoff = DateTime.Now.AddDays(-14);
        var records = _playtimeSnapshotProvider.GetPlaytimeOverview(cutoff);
        if (records.Count == 0)
        {
            return Array.Empty<GameStatsSummary>();
        }

        return records
            .Select(record => ToOverviewSummary(record, configIndex))
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

        var configIndex = LoadConfigIndex();
        var cutoff = DateTime.Now.AddDays(-14);
        var match = records.FirstOrDefault(record =>
            string.Equals(record.GameName, dataKeyOrGameName, StringComparison.OrdinalIgnoreCase));

        return match is null ? null : ToSummary(match, configIndex, cutoff);
    }

    public SessionActivitySnapshot GetSessionActivitySnapshot()
    {
        var snapshot = _playtimeSnapshotProvider.GetSnapshot();
        var source = snapshot.SourcePath ?? string.Empty;
        if (snapshot.Records.Count == 0)
        {
            return new SessionActivitySnapshot(
                new HashSet<SessionActivityKey>(),
                Array.Empty<SessionActivityRecord>(),
                source);
        }

        var configIndex = LoadConfigIndex();
        var keys = new HashSet<SessionActivityKey>();
        var records = new List<SessionActivityRecord>();

        foreach (var item in snapshot.Records)
        {
            var displayName = configIndex.ResolveDisplayName(item.GameName);
            foreach (var session in item.Sessions)
            {
                var record = new SessionActivityRecord(
                    item.GameName,
                    displayName,
                    session.StartTime,
                    session.EndTime,
                    session.DurationMinutes);
                keys.Add(record.Key);
                records.Add(record);
            }
        }

        return new SessionActivitySnapshot(keys, records, source);
    }

    public SessionActivityPreview GetSessionActivityPreview()
    {
        var snapshot = GetSessionActivitySnapshot();
        var today = DateTime.Now.Date;
        var windowStart = today.AddDays(-(PreviewWindowDays - 1));

        var windowSessions = snapshot.Records
            .Where(record => ToLocalTime(record.End) >= windowStart)
            .ToList();

        var games = windowSessions
            .GroupBy(record => record.Key.Game, StringComparer.OrdinalIgnoreCase)
            .Select(group => new SessionGameSummary(
                group.Key,
                group.First().DisplayName,
                group.Count(),
                group.Sum(record => record.DurationMinutes),
                group.Max(record => record.End)))
            .OrderByDescending(item => item.TotalMinutes)
            .ThenByDescending(item => item.LastEnd)
            .ToList();

        var dailyTrend = new List<DailyPlaytimeSummary>();
        for (var day = windowStart; day <= today; day = day.AddDays(1))
        {
            var currentDay = day;
            var minutes = windowSessions
                .Where(record => ToLocalTime(record.End).Date == currentDay)
                .Sum(record => record.DurationMinutes);
            dailyTrend.Add(new DailyPlaytimeSummary(currentDay, minutes));
        }

        return new SessionActivityPreview(games, dailyTrend, windowSessions.Count, PreviewWindowDays, snapshot.Source);
    }

    private static DateTime ToLocalTime(DateTime timestamp)
    {
        return timestamp.Kind == DateTimeKind.Utc ? timestamp.ToLocalTime() : timestamp;
    }

    private StatisticsConfigIndex LoadConfigIndex()
    {
        var configs = (_gameConfiguration.Read().Games ?? new List<GameConfig>())
            .ToDictionary(config => config.DataKey, StringComparer.OrdinalIgnoreCase);
        return StatisticsConfigIndex.Build(configs);
    }

    private static GameStatsSummary ToSummary(
        GamePlaytimeRecord record,
        StatisticsConfigIndex configIndex,
        DateTime cutoff)
    {
        var displayName = configIndex.FindDisplayName(record.GameName);

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

    private static GameStatsSummary ToOverviewSummary(
        GamePlaytimeOverviewRecord record,
        StatisticsConfigIndex configIndex)
    {
        var displayName = configIndex.FindDisplayName(record.GameName);

        return new GameStatsSummary
        {
            GameName = record.GameName,
            DisplayName = displayName,
            TotalMinutes = record.TotalMinutes,
            RecentMinutes = record.RecentMinutes,
            SessionCount = record.SessionCount
        };
    }
}
