namespace GameHelper.Core.Models;

public sealed class GameProcessMatchResult
{
    public required GameConfig Config { get; init; }

    public required string MatchLabel { get; init; }
}
