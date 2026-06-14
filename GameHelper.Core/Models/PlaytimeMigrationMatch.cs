namespace GameHelper.Core.Models;

/// <summary>
/// Describes how a legacy playtime row game value maps to a current DataKey.
/// </summary>
public sealed record PlaytimeMigrationMatch(
    PlaytimeMigrationMatchKind Kind,
    string OriginalGame,
    string? DataKey = null,
    int? Score = null)
{
    /// <summary>
    /// Returns true when migration should replace the legacy game value with <see cref="DataKey"/>.
    /// </summary>
    public bool ShouldRewrite => Kind is PlaytimeMigrationMatchKind.ExactExecutableName or PlaytimeMigrationMatchKind.FuzzyExecutableName;
}

