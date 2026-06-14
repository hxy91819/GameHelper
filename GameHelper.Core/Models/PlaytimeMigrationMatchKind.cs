namespace GameHelper.Core.Models;

/// <summary>
/// Classification for matching a legacy playtime row game value to current configuration.
/// </summary>
public enum PlaytimeMigrationMatchKind
{
    AlreadyDataKey,
    ExactExecutableName,
    FuzzyExecutableName,
    Ambiguous,
    NotFound
}

