using FuzzySharp;
using GameHelper.Core.Models;
using GameHelper.Core.Utilities;

namespace GameHelper.Core.Services;

/// <summary>
/// Matches legacy playtime CSV game values to current configuration DataKeys.
/// </summary>
public sealed class PlaytimeMigrationMatcher
{
    public PlaytimeMigrationMatch Match(string game, IEnumerable<GameConfig> configs)
    {
        ArgumentNullException.ThrowIfNull(configs);

        if (string.IsNullOrWhiteSpace(game))
        {
            return new PlaytimeMigrationMatch(PlaytimeMigrationMatchKind.NotFound, game);
        }

        var candidates = configs
            .Where(config => config is not null && !string.IsNullOrWhiteSpace(config.DataKey))
            .ToArray();

        if (candidates.Any(config => string.Equals(config.DataKey, game, StringComparison.OrdinalIgnoreCase)))
        {
            return new PlaytimeMigrationMatch(PlaytimeMigrationMatchKind.AlreadyDataKey, game, game);
        }

        var normalizedGame = PathNormalizer.NormalizeName(game);
        if (normalizedGame is null)
        {
            return new PlaytimeMigrationMatch(PlaytimeMigrationMatchKind.NotFound, game);
        }

        var executableNameCandidates = candidates
            .Select(config => new
            {
                Config = config,
                NormalizedExecutableName = PathNormalizer.NormalizeName(config.ExecutableName)
            })
            .Where(candidate => candidate.NormalizedExecutableName is not null)
            .ToArray();

        var exactMatches = executableNameCandidates
            .Where(candidate => string.Equals(candidate.NormalizedExecutableName, normalizedGame, StringComparison.OrdinalIgnoreCase))
            .ToArray();

        if (exactMatches.Length == 1)
        {
            return new PlaytimeMigrationMatch(
                PlaytimeMigrationMatchKind.ExactExecutableName,
                game,
                exactMatches[0].Config.DataKey);
        }

        if (exactMatches.Length > 1)
        {
            return new PlaytimeMigrationMatch(PlaytimeMigrationMatchKind.Ambiguous, game);
        }

        var searchText = normalizedGame.ToUpperInvariant();
        var fuzzyMatches = executableNameCandidates
            .Select(candidate =>
            {
                var threshold = GameMatcher.CalculateFuzzyThreshold(candidate.Config.ExecutableName!);
                var score = Fuzz.Ratio(searchText, candidate.NormalizedExecutableName!.ToUpperInvariant());
                return new { candidate.Config, Score = score, Threshold = threshold };
            })
            .Where(match => match.Score >= match.Threshold)
            .OrderByDescending(match => match.Score)
            .ToArray();

        if (fuzzyMatches.Length == 0)
        {
            return new PlaytimeMigrationMatch(PlaytimeMigrationMatchKind.NotFound, game);
        }

        var bestScore = fuzzyMatches[0].Score;
        var bestMatches = fuzzyMatches.Where(match => match.Score == bestScore).ToArray();
        if (bestMatches.Length != 1)
        {
            return new PlaytimeMigrationMatch(PlaytimeMigrationMatchKind.Ambiguous, game, Score: bestScore);
        }

        return new PlaytimeMigrationMatch(
            PlaytimeMigrationMatchKind.FuzzyExecutableName,
            game,
            bestMatches[0].Config.DataKey,
            bestScore);
    }
}
