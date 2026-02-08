namespace GameHelper.ConsoleHost.Api.Models;

public sealed class GameStatsDto
{
    public string GameName { get; set; } = string.Empty;
    public string? DisplayName { get; set; }
    public long TotalMinutes { get; set; }
    public long RecentMinutes { get; set; }
    public int SessionCount { get; set; }
    public List<SessionDto> Sessions { get; set; } = new();
}

public sealed class SessionDto
{
    public DateTime StartTime { get; set; }
    public DateTime EndTime { get; set; }
    public long DurationMinutes { get; set; }
}
