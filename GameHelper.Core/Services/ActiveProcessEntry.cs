namespace GameHelper.Core.Services;

/// <summary>
/// 表示一个当前活跃的游戏进程实例。
/// </summary>
internal readonly record struct ActiveProcessEntry(string DataKey, string? NormalizedName, string? NormalizedPath, int? ProcessId);
