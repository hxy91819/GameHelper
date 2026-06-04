using GameHelper.Core.Models;

namespace GameHelper.Core.Abstractions;

public interface IGameCatalogService
{
    IReadOnlyList<GameEntry> GetAll();

    GameEntry Add(GameEntryUpsertRequest request);

    GameEntry Update(string dataKey, GameEntryUpsertRequest request);

    bool Delete(string dataKey);
}
