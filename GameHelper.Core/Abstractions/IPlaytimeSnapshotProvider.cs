using GameHelper.Core.Models;

namespace GameHelper.Core.Abstractions;

public interface IPlaytimeSnapshotProvider
{
    IReadOnlyList<GamePlaytimeRecord> GetPlaytimeRecords();

    IReadOnlyList<GamePlaytimeOverviewRecord> GetPlaytimeOverview(DateTime recentCutoff)
    {
        var records = GetPlaytimeRecords();
        if (records.Count == 0)
        {
            return Array.Empty<GamePlaytimeOverviewRecord>();
        }

        return records
            .Select(record => new GamePlaytimeOverviewRecord(
                record.GameName,
                record.Sessions.Sum(session => session.DurationMinutes),
                record.Sessions.Where(session => session.EndTime >= recentCutoff).Sum(session => session.DurationMinutes),
                record.Sessions.Count))
            .ToList();
    }

    /// <summary>Cheap history bookmark; null retains legacy file change detection. Read errors must throw.</summary>
    PlaytimeHistoryVersion? GetHistoryVersion() => null;

    /// <summary>Report history and optional scoped journal from the same read transaction.</summary>
    PlaytimeSnapshot GetReportSnapshot() => GetSnapshot();

    PlaytimeSnapshot GetSnapshot()
    {
        return new PlaytimeSnapshot(GetPlaytimeRecords(), null);
    }
}
