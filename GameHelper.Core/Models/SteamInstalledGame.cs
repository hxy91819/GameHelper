namespace GameHelper.Core.Models;

/// <summary>
/// Represents an installed Steam game whose primary executable was resolved locally.
/// </summary>
public sealed record SteamInstalledGame
{
    public required string AppId { get; init; }

    public required string Name { get; init; }

    public required string ExecutablePath { get; init; }
}
