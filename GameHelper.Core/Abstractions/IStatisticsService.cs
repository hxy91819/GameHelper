using GameHelper.Core.Models;

namespace GameHelper.Core.Abstractions;

public interface IStatisticsService
{
    IReadOnlyList<GameStatsSummary> GetOverview();

    GameStatsSummary? GetDetails(string dataKeyOrGameName);

    SessionActivitySnapshot GetSessionActivitySnapshot();

    /// <summary>
    /// 获取按游戏聚合、按日汇总的近期游玩预览（窗口长度由 <see cref="GameHelper.Core.Services.StatisticsService.PreviewWindowDays"/> 定义），
    /// 供监控启动前的历史记录预览使用。
    /// </summary>
    SessionActivityPreview GetSessionActivityPreview();
}
