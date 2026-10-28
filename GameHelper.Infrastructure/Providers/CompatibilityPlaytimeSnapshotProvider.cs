using GameHelper.Core.Abstractions;
using GameHelper.Core.Models;
using GameHelper.Infrastructure.Storage.Sqlite;

namespace GameHelper.Infrastructure.Providers;

/// <summary>Reads the backend selected at activation; compatibility file projections are never a SQLite read dependency.</summary>
public sealed class CompatibilityPlaytimeSnapshotProvider(SessionStorage storage) : IPlaytimeSnapshotProvider
{
    public IReadOnlyList<GamePlaytimeRecord> GetPlaytimeRecords() => GetSnapshot().Records;
    public PlaytimeHistoryVersion? GetHistoryVersion() => storage.Snapshot.GetHistoryVersion();
    public PlaytimeSnapshot GetReportSnapshot() => storage.GetReportSnapshot();
    public PlaytimeSnapshot GetSnapshot()
    {
        try { return storage.Snapshot.GetSnapshot(); }
        catch { return new([], null, ReadFailed: true); }
    }
}
