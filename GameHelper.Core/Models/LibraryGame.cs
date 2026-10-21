namespace GameHelper.Core.Models;

public sealed record LibraryGame
{
    public GameConfig Config { get; init; } = new();
    public string Revision { get; init; } = string.Empty;
    public string State { get; init; } = "未运行";
    public long TotalMinutes { get; init; }
}
