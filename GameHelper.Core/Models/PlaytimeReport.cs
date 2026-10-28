namespace GameHelper.Core.Models;

/// <summary>Program-generated report. Desktop-side providers join local journal completions by DataKey.</summary>
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
    /// <summary>Completed games joined from the local desktop journal, ordered by completion date.</summary>
    public IReadOnlyList<ReportClearedGame> ClearedGames { get; init; } = [];
    /// <summary>True when journal completions were captured together with history by the report provider.</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]
    public bool JournalIncluded { get; init; }
    public int PreviousClearedGames { get; init; }
    public int ClearedChange => ClearedGames.Count - PreviousClearedGames;
}

/// <summary>One local journal completion. The note is display/export only and never sent to the commentary endpoint.</summary>
public sealed record ReportClearedGame(string DataKey, string Name, DateOnly CompletedOn, string Status, string Note = "");
