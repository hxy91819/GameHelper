namespace GameHelper.Core.Models;

public sealed record GameCatalogIntakePreview
{
    public required ExecutableIdentity Executable { get; init; }

    public GameEntry? ExistingEntry { get; init; }

    public required string SuggestedDataKey { get; init; }

    public bool IsRequestedDataKeyAvailable { get; init; }
}
