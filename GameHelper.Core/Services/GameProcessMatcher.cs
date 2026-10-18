using GameHelper.Core.Abstractions;
using GameHelper.Core.Models;
using GameHelper.Core.Utilities;
using Microsoft.Extensions.Logging;

namespace GameHelper.Core.Services;

/// <summary>Shared read-only classification used by process automation and foreground reminders.</summary>
internal sealed class GameProcessMatcher
{
    private readonly AutomationConfigIndex _configIndex;
    private readonly IProcessPathResolver? _pathResolver;
    private readonly ILogger _logger;

    public GameProcessMatcher(AutomationConfigIndex index, IProcessPathResolver? pathResolver, ILogger logger)
    {
        _configIndex = index;
        _pathResolver = pathResolver;
        _logger = logger;
    }

    public GameConfig? Match(
        ProcessEventInfo processInfo,
        string? normalizedName,
        out string? normalizedPath,
        out string matchLabel)
    {
        normalizedPath = null;
        matchLabel = "not matched";

        var pathHint = NormalizeProcessPath(processInfo.ExecutablePath);
        var pathHintConfig = GameMatcher.MatchByPath(pathHint, _configIndex.ByPath, _logger);
        if (pathHintConfig is not null)
        {
            normalizedPath = pathHint;
            matchLabel = "path-hint";
            return pathHintConfig;
        }

        // Every monitor is configured with the same candidate-name gate before events are subscribed.
        var resolvedPath = ResolvePathByPid(processInfo.ProcessId);
        var resolvedPathConfig = GameMatcher.MatchByPath(resolvedPath, _configIndex.ByPath, _logger);
        if (resolvedPathConfig is not null)
        {
            normalizedPath = resolvedPath;
            matchLabel = "path-resolved";
            return resolvedPathConfig;
        }

        if (normalizedName is null ||
            !_configIndex.ByExactName.TryGetValue(normalizedName, out var exactCandidates) ||
            exactCandidates.Length == 0)
        {
            _logger.LogDebug(
                "Start ignored before path/metadata lookup: {Executable} is not a configured candidate",
                processInfo.ExecutableName);
            return null;
        }

        normalizedPath = ResolvePathForCandidate(processInfo, exactCandidates, resolvedPath);

        var config = GameMatcher.MatchByPath(normalizedPath, _configIndex.ByPath, _logger);
        if (config is not null)
        {
            matchLabel = "path";
            return config;
        }

        if (!string.IsNullOrWhiteSpace(normalizedPath) && GameMatcher.IsSystemPath(normalizedPath, _logger))
        {
            _logger.LogDebug("Start rejected before name fallback: system path {Path}", normalizedPath);
            return null;
        }

        if (exactCandidates.Length == 1 &&
            string.IsNullOrWhiteSpace(exactCandidates[0].ExecutablePath) &&
            IsExplicitNameMatch(exactCandidates[0], normalizedName))
        {
            matchLabel = "executable-name";
            return exactCandidates[0];
        }

        var metadataCandidates = GetMetadataCandidates(exactCandidates);
        if (metadataCandidates.Length > 0)
        {
            config = GameMatcher.MatchByMetadata(processInfo, normalizedPath, metadataCandidates, _logger, out matchLabel);
            if (config is not null)
            {
                return config;
            }
        }

        if (exactCandidates.Length == 1 &&
            normalizedPath is null &&
            IsExplicitNameMatch(exactCandidates[0], normalizedName))
        {
            matchLabel = "executable-name (path unavailable)";
            return exactCandidates[0];
        }

        return null;
    }

    private static bool IsExplicitNameMatch(GameConfig config, string? normalizedName)
    {
        return normalizedName is not null &&
            string.Equals(
                PathNormalizer.NormalizeName(config.ExecutableName),
                normalizedName,
                StringComparison.OrdinalIgnoreCase);
    }

    private string? ResolvePathForCandidate(
        ProcessEventInfo processInfo,
        IReadOnlyCollection<GameConfig> exactCandidates,
        string? resolvedPath)
    {
        if (resolvedPath is not null)
        {
            return resolvedPath;
        }

        if (processInfo.ProcessId.HasValue &&
            _pathResolver is not null &&
            exactCandidates.Any(config => !string.IsNullOrWhiteSpace(config.ExecutablePath)))
        {
            return ResolvePathByPid(processInfo.ProcessId);
        }

        return NormalizeProcessPath(processInfo.ExecutablePath);
    }

    // Kernel ETW can supply only "game.exe" as its image hint. Resolving that
    // against our working directory invents an identity and breaks crash recovery.
    private static string? NormalizeProcessPath(string? path) =>
        !string.IsNullOrWhiteSpace(path) && Path.IsPathFullyQualified(path)
            ? PathNormalizer.NormalizePath(path)
            : null;

    private string? ResolvePathByPid(int? processId)
    {
        if (!processId.HasValue || _pathResolver is null || _configIndex.ByPath.Count == 0)
        {
            return null;
        }

        return PathNormalizer.NormalizePath(_pathResolver.TryResolveExecutablePath(processId.Value));
    }

    private NameConfigEntry[] GetMetadataCandidates(IReadOnlyCollection<GameConfig> exactCandidates)
    {
        var candidateSet = new HashSet<GameConfig>(exactCandidates);
        return _configIndex.ByName
            .Where(entry => candidateSet.Contains(entry.Config))
            .ToArray();
    }

}
