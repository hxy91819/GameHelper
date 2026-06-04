namespace GameHelper.Core.Models;

public sealed class GameStatsSummary
{
    public string GameName { get; set; } = string.Empty;

    public string? DisplayName { get; set; }

    public long TotalMinutes { get; set; }

    public long RecentMinutes { get; set; }

    public int SessionCount { get; set; }

    public List<PlaySession> Sessions { get; set; } = new();
}
