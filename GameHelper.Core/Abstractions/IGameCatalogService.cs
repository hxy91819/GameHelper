using GameHelper.Core.Models;

namespace GameHelper.Core.Abstractions;

public interface IGameCatalogService
{
    IReadOnlyList<GameEntry> GetAll();

    GameEntry? FindExistingForAdd(string executableName, string? executablePath);

    string SuggestDataKey(string executableIdentity, string? productName = null);

    bool IsDataKeyAvailable(string dataKey, string? currentDataKey = null);

    GameEntry Add(GameEntryUpsertRequest request);

    GameEntry Save(GameEntryUpsertRequest request);

    GameEntryImportResult Import(GameEntryImportRequest request);

    void RepairStorage();

    GameEntry Update(string dataKey, GameEntryUpsertRequest request);

    bool Delete(string dataKey);
}
