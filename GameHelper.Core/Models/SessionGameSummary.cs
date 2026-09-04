namespace GameHelper.Core.Models;

/// <summary>
/// 按游戏聚合的近期游玩摘要，用于启动监控前的历史记录预览。
/// </summary>
public sealed record SessionGameSummary(
    string GameName,
    string DisplayName,
    int SessionCount,
    long TotalMinutes,
    DateTime LastEnd);
