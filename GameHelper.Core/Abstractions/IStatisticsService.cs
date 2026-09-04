using GameHelper.Core.Models;

namespace GameHelper.Core.Abstractions;

public interface IStatisticsService
{
    IReadOnlyList<GameStatsSummary> GetOverview();

    GameStatsSummary? GetDetails(string dataKeyOrGameName);

    SessionActivitySnapshot GetSessionActivitySnapshot();

    /// <summary>
    /// 获取按游戏聚合、按日汇总的近期游玩预览（最近 7 个自然日），供监控启动前的历史记录预览使用。
    /// </summary>
    SessionActivityPreview GetSessionActivityPreview();
}
