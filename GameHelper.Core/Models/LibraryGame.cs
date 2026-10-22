namespace GameHelper.Core.Models;

public sealed record LibraryGame
{
    public GameConfig Config { get; init; } = new();
    public string Revision { get; init; } = string.Empty;
    public string State { get; init; } = "未运行";
    public long TotalMinutes { get; init; }
    public DateTimeOffset? LastPlayedAt { get; init; }
    public GameInstallation Installation { get; init; } = new(GameInstallationState.Unknown, "尚未检查本地安装位置");
}
