using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using GameHelper.Core.Models;
using GameHelper.Core.Utilities;
using FuzzySharp;
using Microsoft.Extensions.Logging;

namespace GameHelper.Core.Services;

/// <summary>
/// 根据进程事件信息和配置索引，执行 L1 精确路径匹配和 L2 模糊元数据匹配。
/// </summary>
internal sealed class GameMatcher
{
    private const int ShortNameThreshold = 95;
    private const int MediumNameThreshold = 90;
    private const int LongNameThreshold = 80;

    /// <summary>
    /// L1 精确路径匹配。
    /// </summary>
    public GameConfig? MatchByPath(
        string? normalizedPath,
        IReadOnlyDictionary<string, GameConfig> configsByPath,
        ILogger logger)
    {
        if (normalizedPath is null)
        {
            return null;
        }

        if (configsByPath.TryGetValue(normalizedPath, out var config))
        {
            logger.LogInformation("L1 路径匹配成功: {Path} -> {DataKey}", normalizedPath, config.DataKey);
            return config;
        }

        return null;
    }

    /// <summary>
    /// L2 模糊元数据匹配：基于文件 ProductName 或 ExecutableName 进行模糊匹配。
    /// </summary>
    public GameConfig? MatchByMetadata(
        ProcessEventInfo processInfo,
        string? normalizedPath,
        NameConfigEntry[] candidates,
        ILogger logger,
        out string label)
    {
        label = "名称模糊匹配";

        var exePath = normalizedPath ?? processInfo.ExecutablePath;

        // 黑名单检查：拒绝系统路径
        if (!string.IsNullOrWhiteSpace(exePath) && IsSystemPath(exePath, logger))
        {
            logger.LogDebug(
                "L2 匹配拒绝: 系统路径黑名单 - {ExePath}",
                exePath);
            return null;
        }

        var searchText = TryGetProductName(exePath, logger);
        var usedProductName = !string.IsNullOrWhiteSpace(searchText);

        if (!usedProductName)
        {
            searchText = PathNormalizer.NormalizeName(processInfo.ExecutableName);
        }

        if (string.IsNullOrWhiteSpace(searchText) || candidates.Length == 0)
        {
            return null;
        }

        var searchUpper = searchText.ToUpperInvariant();

        var bestCandidates = new List<(NameConfigEntry Entry, int Score, int Threshold)>();
        var bestScore = 0;

        foreach (var entry in candidates)
        {
            var config = entry.Config;
            if (string.IsNullOrWhiteSpace(config.ExecutableName))
            {
                continue;
            }

            // 计算动态阈值
            var threshold = CalculateFuzzyThreshold(config.ExecutableName);
            var score = Fuzz.Ratio(searchUpper, entry.ExecutableNameUpper);

            logger.LogDebug(
                "L2 模糊匹配尝试: {SearchName} vs {ExecutableName} - Score={Score}, Threshold={Threshold}, NameLength={Length}",
                searchText,
                config.ExecutableName,
                score,
                threshold,
                Path.GetFileNameWithoutExtension(config.ExecutableName).Length);

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
            logger.LogDebug("L2 fuzzy match failed: no candidate passed threshold");
            return null;
        }

        (NameConfigEntry Entry, int Score, int Threshold) selected;

        if (bestCandidates.Count == 1)
        {
            selected = bestCandidates[0];
        }
        else if (!string.IsNullOrWhiteSpace(exePath))
        {
            var relatedCandidates = bestCandidates
                .Where(c => IsPathRelated(exePath, c.Entry.Config.ExecutablePath, logger))
                .ToList();

            if (relatedCandidates.Count == 1)
            {
                selected = relatedCandidates[0];
            }
            else
            {
                logger.LogWarning(
                    "L2 match rejected due to ambiguity. SearchName={SearchName}, CandidateCount={Count}, Path={Path}",
                    searchText,
                    bestCandidates.Count,
                    exePath);
                return null;
            }
        }
        else
        {
            logger.LogWarning(
                "L2 match rejected due to ambiguity without process path. SearchName={SearchName}, CandidateCount={Count}",
                searchText,
                bestCandidates.Count);
            return null;
        }

        var bestMatch = selected.Entry.Config;
        var matchedName = selected.Entry.ExecutableName;
        var requiredThreshold = selected.Threshold;

        // Final path relevance validation for the selected candidate.
        if (!string.IsNullOrWhiteSpace(exePath) && !IsPathRelated(exePath, bestMatch.ExecutablePath, logger))
        {
            logger.LogWarning(
                "L2 匹配拒绝: 路径不相关 - ProcessPath={ProcessPath}, ConfigPath={ConfigPath}, Score={Score}, Threshold={Threshold}",
                exePath,
                bestMatch.ExecutablePath ?? "(无)",
                selected.Score,
                requiredThreshold);
            return null;
        }

        // 匹配成功
        var pathValidation = string.IsNullOrWhiteSpace(bestMatch.ExecutablePath)
            ? "legacy-config (path validation skipped)"
            : "path-validation passed";

        label = usedProductName
            ? $"ProductName 模糊匹配 (score {bestScore})"
            : $"ExecutableName 模糊匹配 (score {bestScore})";

        logger.LogInformation(
            "L2 模糊匹配成功: {SearchName} -> {ExecutableName} (score: {Score}, threshold: {Threshold}, DataKey: {DataKey}, {PathValidation})",
            searchText,
            matchedName,
            bestScore,
            requiredThreshold,
            bestMatch.DataKey,
            pathValidation);

        return bestMatch;
    }

    /// <summary>
    /// 根据可执行文件名长度计算模糊匹配阈值。
    /// </summary>
    public static int CalculateFuzzyThreshold(string executableName)
    {
        if (string.IsNullOrWhiteSpace(executableName))
        {
            return ShortNameThreshold;
        }

        var nameWithoutExt = Path.GetFileNameWithoutExtension(executableName);
        var length = nameWithoutExt.Length;

        return length switch
        {
            <= 4 => ShortNameThreshold,
            <= 8 => MediumNameThreshold,
            _ => LongNameThreshold
        };
    }

    /// <summary>
    /// 检查进程路径是否命中系统路径黑名单。
    /// </summary>
    internal bool IsSystemPath(string processPath, ILogger logger)
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
            if (PathNormalizer.TryResolveWindowsPath(processPath, out var windowsPath, out var processDir))
            {
                foreach (var systemPath in systemPaths)
                {
                    if (processDir.StartsWith(systemPath, StringComparison.OrdinalIgnoreCase) ||
                        windowsPath.StartsWith(systemPath, StringComparison.OrdinalIgnoreCase))
                    {
                        return true;
                    }
                }

                return false;
            }

            var normalizedPath = Path.GetFullPath(processPath);

            foreach (var systemPath in systemPaths)
            {
                if (normalizedPath.StartsWith(systemPath, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "系统路径检查失败: {ProcessPath}", processPath);
            return false;
        }
    }

    /// <summary>
    /// 验证进程路径是否与配置路径相关（位于同一目录树）。
    /// </summary>
    internal bool IsPathRelated(string processPath, string? configPath, ILogger logger)
    {
        if (string.IsNullOrWhiteSpace(configPath))
        {
            return true;
        }

        try
        {
            var processHasWindowsRoot = PathNormalizer.TryResolveWindowsPath(processPath, out _, out var processWindowsDir);
            var configHasWindowsRoot = PathNormalizer.TryResolveWindowsPath(configPath, out _, out var configWindowsDir);

            if (processHasWindowsRoot || configHasWindowsRoot)
            {
                if (!processHasWindowsRoot || !configHasWindowsRoot)
                {
                    return false;
                }

                processWindowsDir = PathNormalizer.EnsureTrailingSeparator(processWindowsDir, preferBackslash: true);
                configWindowsDir = PathNormalizer.EnsureTrailingSeparator(configWindowsDir, preferBackslash: true);
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

            processDir = PathNormalizer.EnsureTrailingSeparator(processDir);
            configDir = PathNormalizer.EnsureTrailingSeparator(configDir);

            return processDir.StartsWith(configDir, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "路径验证失败: ProcessPath={ProcessPath}, ConfigPath={ConfigPath}",
                processPath, configPath);
            return false;
        }
    }

    internal string? TryGetProductName(string? executablePath, ILogger logger)
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
            logger.LogDebug(ex, "无法获取 ProductName: {Path}", executablePath);
            return null;
        }
    }
}
