namespace GameHelper.Core.Models;

/// <summary>Explicit library actions; clients never select an alternate server configuration.</summary>
public sealed record GameLibraryRequest
{
    public TrayMenuAction? TrayAction { get; init; }
    public GameLibraryOperation Operation { get; init; }
    public UpdateAppSettingsRequest? Settings { get; init; }
    public Guid? LaunchRequestId { get; init; }
    public string? DataKey { get; init; }
    public string? ExpectedRevision { get; init; }
    public GameConfig? Game { get; init; }
    public string[] SteamAppIds { get; init; } = [];
    public PlaytimeDashboardQuery? Statistics { get; init; }
    public string? Directory { get; init; }
    public IReadOnlyList<LocalGameCandidate> LocalGames { get; init; } = [];
    public SessionReviewQuery? SessionReviews { get; init; }
    public Guid? SessionId { get; init; }
    public SaveSessionRating? SessionRating { get; init; }
    public ReportPeriod? ReportPeriod { get; init; }
}
