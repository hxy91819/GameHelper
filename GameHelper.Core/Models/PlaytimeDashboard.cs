namespace GameHelper.Core.Models;

/// <summary>Read-only dashboard query. Zero days selects all history; game/date filtering applies only to the focused summary and optional legacy session page.</summary>
public sealed record PlaytimeDashboardQuery(int Days = 30, string? SessionGameKey = null, int Offset = 0, DateOnly? FocusDate = null, bool IncludeRecent = true);

public sealed record DashboardDay(DateOnly Date, long Minutes, int Sessions);
public sealed record DashboardBucket(string Label, DateOnly Start, DateOnly End, long Minutes);
public sealed record DashboardGame(string DataKey, string Name, long Minutes, int Sessions, DateTime LastPlayed);
public sealed record DashboardSession(string DataKey, string Name, DateTime Start, DateTime End, long Minutes);

/// <summary>Focused play rhythm, with Monday-first weekday totals over the same persisted minutes.</summary>
public sealed record DashboardRhythm(long Minutes, int Sessions, int ActiveDays, long LongestSessionMinutes, IReadOnlyList<long> WeekdayMinutes);

/// <summary>Bounded aggregates over one consistent completed-playtime snapshot, never live timer estimates.</summary>
public sealed record PlaytimeDashboard
{
    public DashboardRhythm Rhythm { get; init; } = new(0, 0, 0, 0, new long[7]);
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
