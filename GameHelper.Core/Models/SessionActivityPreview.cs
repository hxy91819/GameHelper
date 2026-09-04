namespace GameHelper.Core.Models;

/// <summary>
/// 历史记录预览的聚合视图：近 <see cref="WindowDays"/> 个自然日内的按游戏汇总与每日时长趋势。
/// </summary>
public sealed record SessionActivityPreview(
    IReadOnlyList<SessionGameSummary> Games,
    IReadOnlyList<DailyPlaytimeSummary> DailyTrend,
    int SessionCount,
    int WindowDays,
    string Source);
