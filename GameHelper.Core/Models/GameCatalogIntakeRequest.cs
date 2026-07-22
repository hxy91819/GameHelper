namespace GameHelper.Core.Models;

/// <summary>
/// Describes one executable candidate entering the Game Catalog.
/// </summary>
public sealed record GameCatalogIntakeRequest
{
    public required ExecutableIdentity Executable { get; init; }

    public string? DataKey { get; init; }

    public string? ProductName { get; init; }

    public string? DisplayName { get; init; }

    public bool IsEnabled { get; init; } = true;

    /// <summary>
    /// Explicit HDR choice. When omitted for an existing entry, its current choice is preserved.
    /// </summary>
    public bool? HdrEnabled { get; init; }
}
