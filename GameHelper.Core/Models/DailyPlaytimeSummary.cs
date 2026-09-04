namespace GameHelper.Core.Models;

/// <summary>
/// 单个自然日的游玩总时长（本地时间按会话结束时间归属），用于每日趋势展示。
/// </summary>
public sealed record DailyPlaytimeSummary(DateTime Date, long Minutes);
