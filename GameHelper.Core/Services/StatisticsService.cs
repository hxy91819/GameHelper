using GameHelper.Core.Abstractions;
using GameHelper.Core.Models;

namespace GameHelper.Core.Services;

public sealed class StatisticsService : IStatisticsService
{
    /// <summary>历史记录预览覆盖的最近自然日数量（含今天）。</summary>
    public const int PreviewWindowDays = 60;

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
        return GetSessionActivityPreview(GetSessionActivitySnapshot());
    }

    public SessionActivityPreview GetSessionActivityPreview(SessionActivitySnapshot snapshot)
    {
        var today = DateTime.Now.Date;
        var windowStart = today.AddDays(-(PreviewWindowDays - 1));

        // 窗口过滤后再聚合；单遍分桶避免对同一集合做 O(窗口) 次全量扫描。
        var windowMinutes = new long[PreviewWindowDays];
        var games = new Dictionary<string, SessionGameSummary>(StringComparer.OrdinalIgnoreCase);
        var windowSessionCount = 0;
        foreach (var record in snapshot.Records)
        {
            var endLocal = ToLocalTime(record.End);
            if (endLocal < windowStart)
            {
                continue;
            }

            windowSessionCount++;
            var dayIndex = (endLocal.Date - windowStart).Days;
            if (dayIndex >= 0 && dayIndex < PreviewWindowDays)
            {
                windowMinutes[dayIndex] += record.DurationMinutes;
            }

            if (games.TryGetValue(record.Key.Game, out var summary))
            {
                games[record.Key.Game] = summary with
                {
                    SessionCount = summary.SessionCount + 1,
                    TotalMinutes = summary.TotalMinutes + record.DurationMinutes,
                    LastEnd = endLocal > summary.LastEnd ? endLocal : summary.LastEnd
                };
            }
            else
            {
                games[record.Key.Game] = new SessionGameSummary(
                    record.Key.Game,
                    record.DisplayName,
                    1,
                    record.DurationMinutes,
                    endLocal);
            }
        }

        var dailyTrend = new List<DailyPlaytimeSummary>(PreviewWindowDays);
        for (var i = 0; i < PreviewWindowDays; i++)
        {
            dailyTrend.Add(new DailyPlaytimeSummary(windowStart.AddDays(i), windowMinutes[i]));
        }

        return new SessionActivityPreview(
            games.Values
                .OrderByDescending(item => item.TotalMinutes)
                .ThenByDescending(item => item.LastEnd)
                .ToList(),
            dailyTrend,
            windowSessionCount,
            PreviewWindowDays,
            snapshot.Source);
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
