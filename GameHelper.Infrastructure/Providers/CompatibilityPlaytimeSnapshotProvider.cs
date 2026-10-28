using GameHelper.Core.Abstractions;
using GameHelper.Core.Models;
using GameHelper.Infrastructure.Storage.Sqlite;

namespace GameHelper.Infrastructure.Providers;

/// <summary>Sync retains its CSV/mtime contract; a failed projection cannot be uploaded as current history.</summary>
public sealed class CompatibilityPlaytimeSnapshotProvider(SessionStorage storage, string directory) : IPlaytimeSnapshotProvider
{
    private readonly FilePlaytimeSnapshotProvider _files = new(directory);
    public IReadOnlyList<GamePlaytimeRecord> GetPlaytimeRecords() => GetSnapshot().Records;
    public PlaytimeSnapshot GetSnapshot()
    {
        try { storage.FlushProjection(); return _files.GetSnapshot(); }
        catch { return new([], null, ReadFailed: true); }
    }
}
