namespace GameHelper.Core.Models;

/// <summary>Explicit library actions; clients never select an alternate server configuration.</summary>
public sealed record GameLibraryRequest
{
    public GameLibraryOperation Operation { get; init; }
    public string? DataKey { get; init; }
    public string? ExpectedRevision { get; init; }
    public GameConfig? Game { get; init; }
    public string[] SteamAppIds { get; init; } = [];
}
