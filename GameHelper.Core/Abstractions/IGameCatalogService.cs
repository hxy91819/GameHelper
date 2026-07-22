using GameHelper.Core.Models;

namespace GameHelper.Core.Abstractions;

public interface IGameCatalogService
{
    IReadOnlyList<GameEntry> List();

    GameCatalogIntakePreview PreviewIntake(GameCatalogIntakeRequest request);

    GameCatalogIntakeResult Intake(GameCatalogIntakeRequest request);

    IReadOnlyList<GameCatalogIntakeResult> BatchIntake(IEnumerable<GameCatalogIntakeRequest> requests);

    GameEntry Update(string dataKey, GameCatalogUpdateRequest request);

    bool Remove(string dataKey);
}
