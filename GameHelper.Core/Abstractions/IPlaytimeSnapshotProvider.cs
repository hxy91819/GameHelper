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

    PlaytimeSnapshot GetSnapshot()
    {
        return new PlaytimeSnapshot(GetPlaytimeRecords(), null);
    }
}
