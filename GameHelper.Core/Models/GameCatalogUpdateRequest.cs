namespace GameHelper.Core.Models;

public sealed record GameCatalogUpdateRequest
{
    public ExecutableIdentity? Executable { get; init; }

    public string? DisplayName { get; init; }

    public bool ClearDisplayName { get; init; }

    public bool? IsEnabled { get; init; }

    public bool? HdrEnabled { get; init; }
}
