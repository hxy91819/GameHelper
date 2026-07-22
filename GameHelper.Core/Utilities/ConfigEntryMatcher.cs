using GameHelper.Core.Models;

namespace GameHelper.Core.Utilities;

/// <summary>
/// Shared matching policy for add/import flows.
/// Path-exact match wins; name-only fallback is allowed only for a single candidate without path.
/// A same Steam install directory (<c>steamapps\common\&lt;dir&gt;</c>) soft match resolves the
/// launcher-vs-game-binary ambiguity (e.g. <c>crs-handler.exe</c> vs <c>SB-Win64-Shipping.exe</c>).
/// </summary>
public static class ConfigEntryMatcher
{
    private const string SteamCommonSegment = "steamapps/common/";

    public static GameConfig? FindExistingForIntake(
        IEnumerable<GameConfig> configs,
        ExecutableIdentity executable)
    {
        ArgumentNullException.ThrowIfNull(executable);
        var candidates = configs?.Where(c => c is not null).ToList() ?? new List<GameConfig>();
        var normalizedPath = NormalizePath(executable.Path);

        if (!string.IsNullOrWhiteSpace(normalizedPath))
        {
            var byPath = candidates.FirstOrDefault(cfg =>
                !string.IsNullOrWhiteSpace(cfg.ExecutablePath) &&
                string.Equals(NormalizePath(cfg.ExecutablePath), normalizedPath, StringComparison.OrdinalIgnoreCase));
            if (byPath is not null)
            {
                return byPath;
            }
        }

        var sameNameCandidates = candidates
            .Where(cfg => string.Equals(cfg.ExecutableName, executable.Name, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (sameNameCandidates.Count == 1 && string.IsNullOrWhiteSpace(sameNameCandidates[0].ExecutablePath))
        {
            return sameNameCandidates[0];
        }

        // Same Steam install directory soft match: resolves launcher-vs-game-binary ambiguity
        // for games installed under steamapps\common\<dir>. Only applies when both the incoming
        // identity and candidates carry paths; multiple matches are treated as ambiguous.
        var incomingSteamDir = TryGetSteamInstallDir(executable.Path);
        if (incomingSteamDir is not null)
        {
            var sameSteamDirCandidates = candidates
                .Where(cfg => TryGetSteamInstallDir(cfg.ExecutablePath) is not null &&
                    string.Equals(TryGetSteamInstallDir(cfg.ExecutablePath), incomingSteamDir, StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (sameSteamDirCandidates.Count == 1)
            {
                return sameSteamDirCandidates[0];
            }
        }

        return null;
    }

    public static string NormalizePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return string.Empty;
        }

        try
        {
            return Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
        catch
        {
            return path.Trim().TrimEnd('\\', '/');
        }
    }

    /// <summary>
    /// Extracts the Steam install directory segment from a path, i.e. the <c>&lt;dir&gt;</c>
    /// in <c>.../steamapps/common/&lt;dir&gt;/...</c>. Returns null when the path does not
    /// contain a <c>steamapps/common</c> segment.
    /// </summary>
    internal static string? TryGetSteamInstallDir(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        // Normalize to a forward-slash form for a single, cross-platform search.
        var normalized = NormalizePath(path)
            .Replace('\\', '/')
            .TrimEnd('/');

        var segmentIndex = normalized.IndexOf(SteamCommonSegment, StringComparison.OrdinalIgnoreCase);
        if (segmentIndex < 0)
        {
            return null;
        }

        var afterSegment = normalized[(segmentIndex + SteamCommonSegment.Length)..];
        if (string.IsNullOrWhiteSpace(afterSegment))
        {
            return null;
        }

        // The install directory is the first path component after steamapps/common/.
        var dirEnd = afterSegment.IndexOf('/');
        return dirEnd < 0 ? afterSegment : afterSegment[..dirEnd];
    }
}
