using GameHelper.Core.Models;

namespace GameHelper.Core.Abstractions;

public interface IStatisticsService
{
    IReadOnlyList<GameStatsSummary> GetOverview();

    GameStatsSummary? GetDetails(string dataKeyOrGameName);
}
