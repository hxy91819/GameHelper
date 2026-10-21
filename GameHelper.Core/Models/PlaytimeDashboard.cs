namespace GameHelper.Core.Models;

/// <summary>Read-only dashboard query. Zero days selects all history; game filtering applies to the session table.</summary>
public sealed record PlaytimeDashboardQuery(int Days = 30, string? SessionGameKey = null, int Offset = 0);

public sealed record DashboardDay(DateOnly Date, long Minutes, int Sessions);
public sealed record DashboardBucket(string Label, DateOnly Start, DateOnly End, long Minutes);
public sealed record DashboardGame(string DataKey, string Name, long Minutes, int Sessions, DateTime LastPlayed);
public sealed record DashboardSession(string DataKey, string Name, DateTime Start, DateTime End, long Minutes);

/// <summary>Bounded aggregates over one consistent completed-playtime snapshot, never live timer estimates.</summary>
public sealed record PlaytimeDashboard
{
    public DateOnly Today { get; init; }
    public DateOnly Start { get; init; }
    public int Days { get; init; }
    public long Minutes { get; init; }
    public long PreviousMinutes { get; init; }
    public int Sessions { get; init; }
    public int ActiveDays { get; init; }
    public int GamesPlayed { get; init; }
    public long LongestSessionMinutes { get; init; }
    public IReadOnlyList<DashboardBucket> Trend { get; init; } = [];
    public IReadOnlyList<DashboardDay> Calendar { get; init; } = [];
    public IReadOnlyList<DashboardGame> Games { get; init; } = [];
    public IReadOnlyList<DashboardSession> Recent { get; init; } = [];
    public int RecentTotal { get; init; }
    public int Offset { get; init; }
    public int PageSize { get; init; } = 30;
}
