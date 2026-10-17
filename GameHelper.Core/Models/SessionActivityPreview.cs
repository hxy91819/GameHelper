namespace GameHelper.Core.Models;

/// <summary>
/// 历史记录预览的聚合视图：近 <see cref="WindowDays"/> 个自然日内的按游戏汇总与每日时长趋势。
/// </summary>
/// <param name="PreviousHalfGames">
/// 窗口前半（早段）中至少有一场会话（按结束日期归属）的不同游戏数。
/// 半窗划分与渲染端 <c>DailyTrendChartRenderer.CompareHalves</c> 一致：前半窗为窗口前
/// <c>WindowDays / 2</c> 天，其余为后半（近段）；两侧数量均为 0 表示窗口内无游玩。
/// </param>
/// <param name="RecentHalfGames">窗口后半（近段）中至少有一场会话的不同游戏数。</param>
public sealed record SessionActivityPreview(
    IReadOnlyList<SessionGameSummary> Games,
    IReadOnlyList<DailyPlaytimeSummary> DailyTrend,
    int SessionCount,
    int WindowDays,
    string Source,
    int PreviousHalfGames,
    int RecentHalfGames);
