namespace GameHelper.Core.Models;

/// <summary>Program-generated report. Completion data can be joined by DataKey by a desktop-side provider.</summary>
public sealed record PlaytimeReport
{
    public ReportPeriod Period { get; init; } = new(2000);
    public bool IsPartial { get; init; }
    public long Minutes { get; init; }
    public int ActiveDays { get; init; }
    public int GamesPlayed { get; init; }
    public int Sessions { get; init; }
    public long PreviousMinutes { get; init; }
    public int PreviousActiveDays { get; init; }
    public int PreviousGamesPlayed { get; init; }
    public long MinutesChange => Minutes - PreviousMinutes;
    public double? MinutesChangePercent => PreviousMinutes == 0 ? null : 100d * MinutesChange / PreviousMinutes;
    public DashboardGame? LongestSession { get; init; }
    public IReadOnlyList<DashboardGame> TopGames { get; init; } = [];
    public IReadOnlyList<DashboardBucket> Months { get; init; } = [];
    public IReadOnlyList<DashboardBucket> Weeks { get; init; } = [];
    /// <summary>24 buckets of persisted minutes assigned to the local session start hour.</summary>
    public IReadOnlyList<long> HourMinutes { get; init; } = new long[24];
    public int? MostPlayedHour { get; init; }
    public IReadOnlyList<string> NewGameKeys { get; init; } = [];
}
