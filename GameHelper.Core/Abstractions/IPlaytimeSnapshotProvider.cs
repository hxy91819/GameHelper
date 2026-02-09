using GameHelper.Core.Models;

namespace GameHelper.Core.Abstractions;

public interface IPlaytimeSnapshotProvider
{
    IReadOnlyList<GamePlaytimeRecord> GetPlaytimeRecords();
}
