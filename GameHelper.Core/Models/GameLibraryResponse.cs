namespace GameHelper.Core.Models;

public sealed record GameLibraryResponse
{
    public AppSettingsSnapshot? Settings { get; init; }
    public MonitorModeSnapshot? DdcSnapshot { get; init; }
    public SyncStatusInfo? SyncStatus { get; init; }
    public GameHelper.Core.Abstractions.SyncOutcome? SyncOutcome { get; init; }
    public bool Success { get; init; }
    public string? Error { get; init; }
    public string? Message { get; init; }
    public string ConfigPath { get; init; } = string.Empty;
    public bool MonitorRunning { get; init; }
    public int RuntimeProcessId { get; init; }
    public IReadOnlyList<LibraryGame> Games { get; init; } = [];
    public IReadOnlyList<SteamInstalledGame> SteamGames { get; init; } = [];
    public PlaytimeDashboard? Statistics { get; init; }
    public LocalGameScan? LocalScan { get; init; }
    public IReadOnlyList<SessionRatingPoint> RatingTimeline { get; init; } = [];
    public SessionReviewPage? SessionReviews { get; init; }
    public RatedSession? RatedSession { get; init; }
    public PlaytimeReport? Report { get; init; }
}
