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

        var configIndex = LoadConfigIndex();
        var cutoff = DateTime.Now.AddDays(-14);

        return records
            .Select(record => ToSummary(record, configIndex, cutoff))
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

    private StatisticsConfigIndex LoadConfigIndex()
    {
        return StatisticsConfigIndex.Build(_configProvider.Load());
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

}
