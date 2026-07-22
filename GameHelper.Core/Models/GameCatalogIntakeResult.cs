namespace GameHelper.Core.Models;

public sealed record GameCatalogIntakeResult
{
    public required GameEntry Entry { get; init; }

    public bool WasAdded { get; init; }

    public string? PreviousExecutablePath { get; init; }
}
