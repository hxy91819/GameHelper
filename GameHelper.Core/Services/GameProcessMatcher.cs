using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using FuzzySharp;
using GameHelper.Core.Abstractions;
using GameHelper.Core.Models;
using Microsoft.Extensions.Logging;

namespace GameHelper.Core.Services;

public sealed class GameProcessMatcher : IGameProcessMatcher
{
    private readonly ILogger<GameProcessMatcher> _logger;

    private const int ShortNameThreshold = 95;
    private const int MediumNameThreshold = 90;
    private const int LongNameThreshold = 80;

    public GameProcessMatcher(ILogger<GameProcessMatcher> logger)
    {
        _logger = logger;
    }

    public GameProcessMatchSnapshot CreateSnapshot(IReadOnlyDictionary<string, GameConfig> configs)
    {
        var enabledConfigs = configs.Values
            .Where(config => config is not null && config.IsEnabled)
            .ToList();

        var dataKeyMap = new Dictionary<string, GameConfig>(StringComparer.OrdinalIgnoreCase);
        var pathMap = new Dictionary<string, GameConfig>(StringComparer.OrdinalIgnoreCase);
        var nameStats = new Dictionary<string, (int Count, int MissingPathCount)>(StringComparer.OrdinalIgnoreCase);
        var nameEntries = new List<GameProcessNameEntry>();

        foreach (var config in enabledConfigs)
        {
            if (!string.IsNullOrWhiteSpace(config.DataKey) && !dataKeyMap.TryAdd(config.DataKey, config))
            {
                _logger.LogWarning("Duplicate DataKey detected while building indexes: {DataKey}. Keeping the first entry.", config.DataKey);
            }

            var normalizedPath = NormalizePath(config.ExecutablePath);
            if (normalizedPath is not null)
            {
                if (!pathMap.TryAdd(normalizedPath, config))
                {
                    _logger.LogWarning("Duplicate executable path detected while building indexes: {Path}. Keeping the first entry.", normalizedPath);
                }
            }

            var normalizedName = NormalizeName(config.ExecutableName);
            if (normalizedName is null)
            {
                continue;
            }

            if (!nameStats.TryGetValue(normalizedName, out var stat))
            {
                stat = (0, 0);
            }

            stat.Count++;
            if (normalizedPath is null)
            {
                stat.MissingPathCount++;
            }

            nameStats[normalizedName] = stat;
            nameEntries.Add(new GameProcessNameEntry(normalizedName, normalizedName.ToUpperInvariant(), config));
        }

        foreach (var (name, stat) in nameStats.Where(kv => kv.Value.Count > 1))
        {
            if (stat.MissingPathCount > 0)
            {
                _logger.LogWarning(
                    "Duplicate executable name detected while building indexes: {Name}. DuplicateCount={Count}, MissingPathCount={MissingPathCount}. Name-only matching may become ambiguous.",
                    name,
                    stat.Count,
                    stat.MissingPathCount);
            }
            else
            {
                _logger.LogDebug(
                    "Duplicate executable name detected while building indexes: {Name}. DuplicateCount={Count}. All entries have ExecutablePath, path-first matching remains deterministic.",
                    name,
                    stat.Count);
            }
        }

        return new GameProcessMatchSnapshot
        {
            Configs = enabledConfigs
                .Where(config => !string.IsNullOrWhiteSpace(config.EntryId))
                .ToDictionary(config => config.EntryId, config => config, StringComparer.OrdinalIgnoreCase),
            ConfigsByDataKey = dataKeyMap,
            ConfigsByPath = pathMap,
            NameEntries = nameEntries
        };
    }

    public GameProcessMatchResult? Match(ProcessEventInfo processInfo, GameProcessMatchSnapshot snapshot)
    {
        var normalizedPath = NormalizePath(processInfo.ExecutablePath);
        if (normalizedPath is not null && snapshot.ConfigsByPath.TryGetValue(normalizedPath, out var pathConfig))
        {
            _logger.LogInformation("L1 路径匹配成功: {Path} -> {DataKey}", normalizedPath, pathConfig.DataKey);
            return new GameProcessMatchResult
            {
                Config = pathConfig,
                MatchLabel = "路径匹配"
            };
        }

        return MatchByMetadata(processInfo, normalizedPath, snapshot);
    }

    public string? NormalizeName(string? executableName)
    {
        if (string.IsNullOrWhiteSpace(executableName))
        {
            return null;
        }

        var name = Path.GetFileName(executableName.Trim());
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        if (!name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            name += ".exe";
        }

        return name;
    }

    public string? NormalizePath(string? executablePath)
    {
        if (string.IsNullOrWhiteSpace(executablePath))
        {
            return null;
        }

        if (TryResolveWindowsPath(executablePath, out var windowsPath, out _))
        {
            if (windowsPath.Length > 3 &&
                windowsPath[1] == ':' &&
                windowsPath.EndsWith("\\", StringComparison.Ordinal))
            {
                windowsPath = windowsPath.TrimEnd('\\');
            }
            else if (windowsPath.StartsWith("\\\\", StringComparison.Ordinal) &&
                     windowsPath.EndsWith("\\", StringComparison.Ordinal))
            {
                windowsPath = windowsPath.TrimEnd('\\');
            }

            return windowsPath;
        }

        try
        {
            return Path.GetFullPath(executablePath)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
        catch
        {
            return executablePath.Trim();
        }
    }

    private GameProcessMatchResult? MatchByMetadata(ProcessEventInfo processInfo, string? normalizedPath, GameProcessMatchSnapshot snapshot)
    {
        var exePath = normalizedPath ?? processInfo.ExecutablePath;
        if (!string.IsNullOrWhiteSpace(exePath) && IsSystemPath(exePath))
        {
            _logger.LogDebug("L2 匹配拒绝: 系统路径黑名单 - {ExePath}", exePath);
            return null;
        }

        var searchText = TryGetProductName(exePath);
        var usedProductName = !string.IsNullOrWhiteSpace(searchText);
        if (!usedProductName)
        {
            searchText = NormalizeName(processInfo.ExecutableName);
        }

        if (string.IsNullOrWhiteSpace(searchText) || snapshot.NameEntries.Count == 0)
        {
            return null;
        }

        var searchUpper = searchText.ToUpperInvariant();
        var bestCandidates = new List<(GameProcessNameEntry Entry, int Score, int Threshold)>();
        var bestScore = 0;

        foreach (var entry in snapshot.NameEntries)
        {
            var threshold = CalculateFuzzyThreshold(entry.ExecutableName);
            var score = Fuzz.Ratio(searchUpper, entry.ExecutableNameUpper);

            if (score < threshold)
            {
                continue;
            }

            if (score > bestScore)
            {
                bestScore = score;
                bestCandidates.Clear();
            }

            if (score == bestScore)
            {
                bestCandidates.Add((entry, score, threshold));
            }
        }

        if (bestCandidates.Count == 0)
        {
            return null;
        }

        (GameProcessNameEntry Entry, int Score, int Threshold) selected;
        if (bestCandidates.Count == 1)
        {
            selected = bestCandidates[0];
        }
        else if (!string.IsNullOrWhiteSpace(exePath))
        {
            var relatedCandidates = bestCandidates
                .Where(candidate => IsPathRelated(exePath, candidate.Entry.Config.ExecutablePath))
                .ToList();

            if (relatedCandidates.Count != 1)
            {
                _logger.LogWarning(
                    "L2 match rejected due to ambiguity. SearchName={SearchName}, CandidateCount={Count}, Path={Path}",
                    searchText,
                    bestCandidates.Count,
                    exePath);
                return null;
            }

            selected = relatedCandidates[0];
        }
        else
        {
            _logger.LogWarning(
                "L2 match rejected due to ambiguity without process path. SearchName={SearchName}, CandidateCount={Count}",
                searchText,
                bestCandidates.Count);
            return null;
        }

        if (!string.IsNullOrWhiteSpace(exePath) && !IsPathRelated(exePath, selected.Entry.Config.ExecutablePath))
        {
            _logger.LogWarning(
                "L2 匹配拒绝: 路径不相关 - ProcessPath={ProcessPath}, ConfigPath={ConfigPath}, Score={Score}, Threshold={Threshold}",
                exePath,
                selected.Entry.Config.ExecutablePath ?? "(无)",
                selected.Score,
                selected.Threshold);
            return null;
        }

        var label = usedProductName
            ? $"ProductName 模糊匹配 (score {bestScore})"
            : $"ExecutableName 模糊匹配 (score {bestScore})";

        _logger.LogInformation(
            "L2 模糊匹配成功: {SearchName} -> {ExecutableName} (score: {Score}, threshold: {Threshold}, DataKey: {DataKey})",
            searchText,
            selected.Entry.ExecutableName,
            bestScore,
            selected.Threshold,
            selected.Entry.Config.DataKey);

        return new GameProcessMatchResult
        {
            Config = selected.Entry.Config,
            MatchLabel = label
        };
    }

    private string? TryGetProductName(string? executablePath)
    {
        if (string.IsNullOrWhiteSpace(executablePath))
        {
            return null;
        }

        try
        {
            var info = FileVersionInfo.GetVersionInfo(executablePath);
            return info.ProductName;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "无法获取 ProductName: {Path}", executablePath);
            return null;
        }
    }

    private static int CalculateFuzzyThreshold(string executableName)
    {
        var name = Path.GetFileNameWithoutExtension(executableName);
        return name.Length switch
        {
            <= 4 => ShortNameThreshold,
            <= 8 => MediumNameThreshold,
            _ => LongNameThreshold
        };
    }

    private bool IsSystemPath(string processPath)
    {
        if (string.IsNullOrWhiteSpace(processPath))
        {
            return false;
        }

        var systemPaths = new[]
        {
            @"C:\Windows\System32\",
            @"C:\Windows\SysWOW64\",
            @"C:\Windows\"
        };

        try
        {
            if (TryResolveWindowsPath(processPath, out var windowsPath, out var processDir))
            {
                return systemPaths.Any(systemPath =>
                    processDir.StartsWith(systemPath, StringComparison.OrdinalIgnoreCase) ||
                    windowsPath.StartsWith(systemPath, StringComparison.OrdinalIgnoreCase));
            }

            var normalizedPath = Path.GetFullPath(processPath);
            return systemPaths.Any(systemPath => normalizedPath.StartsWith(systemPath, StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "系统路径检查失败: {ProcessPath}", processPath);
            return false;
        }
    }

    private bool IsPathRelated(string processPath, string? configPath)
    {
        if (string.IsNullOrWhiteSpace(configPath))
        {
            return true;
        }

        try
        {
            var processHasWindowsRoot = TryResolveWindowsPath(processPath, out _, out var processWindowsDir);
            var configHasWindowsRoot = TryResolveWindowsPath(configPath, out _, out var configWindowsDir);

            if (processHasWindowsRoot || configHasWindowsRoot)
            {
                if (!processHasWindowsRoot || !configHasWindowsRoot)
                {
                    return false;
                }

                processWindowsDir = EnsureTrailingSeparator(processWindowsDir, preferBackslash: true);
                configWindowsDir = EnsureTrailingSeparator(configWindowsDir, preferBackslash: true);
                return processWindowsDir.StartsWith(configWindowsDir, StringComparison.OrdinalIgnoreCase);
            }

            var normalizedProcessPath = Path.GetFullPath(processPath);
            var normalizedConfigPath = Path.GetFullPath(configPath);
            var processDir = Path.GetDirectoryName(normalizedProcessPath);
            var configDir = Path.GetDirectoryName(normalizedConfigPath);

            if (string.IsNullOrEmpty(processDir) || string.IsNullOrEmpty(configDir))
            {
                return false;
            }

            processDir = EnsureTrailingSeparator(processDir);
            configDir = EnsureTrailingSeparator(configDir);
            return processDir.StartsWith(configDir, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "路径验证失败: ProcessPath={ProcessPath}, ConfigPath={ConfigPath}", processPath, configPath);
            return false;
        }
    }

    private static bool TryResolveWindowsPath(string path, out string normalizedPath, out string directory)
    {
        normalizedPath = string.Empty;
        directory = string.Empty;

        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        var trimmed = path.Trim();
        if (IsWindowsDrivePath(trimmed))
        {
            return TryResolveDrivePath(trimmed, out normalizedPath, out directory);
        }

        if (IsWindowsUncPath(trimmed))
        {
            return TryResolveUncPath(trimmed, out normalizedPath, out directory);
        }

        return false;
    }

    private static bool IsWindowsDrivePath(string path)
    {
        return path.Length >= 3 &&
               char.IsLetter(path[0]) &&
               path[1] == ':' &&
               (path[2] == '\\' || path[2] == '/');
    }

    private static bool IsWindowsUncPath(string path)
    {
        return path.StartsWith(@"\\", StringComparison.Ordinal) || path.StartsWith("//", StringComparison.Ordinal);
    }

    private static bool TryResolveDrivePath(string path, out string normalizedPath, out string directory)
    {
        normalizedPath = string.Empty;
        directory = string.Empty;

        var segments = path.Substring(2)
            .Split(new[] { '\\', '/' }, StringSplitOptions.RemoveEmptyEntries);
        var resolved = new List<string>();

        foreach (var segment in segments)
        {
            if (segment == ".")
            {
                continue;
            }

            if (segment == "..")
            {
                if (resolved.Count == 0)
                {
                    return false;
                }

                resolved.RemoveAt(resolved.Count - 1);
                continue;
            }

            resolved.Add(segment);
        }

        normalizedPath = $"{char.ToUpperInvariant(path[0])}:\\{string.Join("\\", resolved)}";
        directory = resolved.Count == 0
            ? $"{char.ToUpperInvariant(path[0])}:\\"
            : $"{char.ToUpperInvariant(path[0])}:\\{string.Join("\\", resolved.Take(resolved.Count - 1))}\\";
        return true;
    }

    private static bool TryResolveUncPath(string path, out string normalizedPath, out string directory)
    {
        normalizedPath = string.Empty;
        directory = string.Empty;

        var trimmed = path.Replace('/', '\\');
        var segments = trimmed.TrimStart('\\')
            .Split('\\', StringSplitOptions.RemoveEmptyEntries);

        if (segments.Length < 2)
        {
            return false;
        }

        var resolved = new List<string> { segments[0], segments[1] };
        foreach (var segment in segments.Skip(2))
        {
            if (segment == ".")
            {
                continue;
            }

            if (segment == "..")
            {
                if (resolved.Count <= 2)
                {
                    return false;
                }

                resolved.RemoveAt(resolved.Count - 1);
                continue;
            }

            resolved.Add(segment);
        }

        normalizedPath = @"\\" + string.Join("\\", resolved);
        directory = resolved.Count <= 2
            ? @"\\" + string.Join("\\", resolved) + "\\"
            : @"\\" + string.Join("\\", resolved.Take(resolved.Count - 1)) + "\\";
        return true;
    }

    private static string EnsureTrailingSeparator(string path, bool preferBackslash = false)
    {
        var separator = preferBackslash ? '\\' : Path.DirectorySeparatorChar;
        if (path.EndsWith(separator))
        {
            return path;
        }

        return path + separator;
    }
}
