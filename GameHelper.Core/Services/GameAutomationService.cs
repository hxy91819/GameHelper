using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using GameHelper.Core.Abstractions;
using GameHelper.Core.Models;
using GameHelper.Core.Utilities;
using FuzzySharp;
using Microsoft.Extensions.Logging;

namespace GameHelper.Core.Services
{
    /// <summary>
    /// Coordinates process monitoring, playtime tracking, and HDR toggling based on enabled games.
    /// Lifecycle: call <see cref="Start"/> to subscribe to monitor events and load config,
    /// and <see cref="Stop"/> to unsubscribe and flush any active sessions.
    /// </summary>
    public sealed class GameAutomationService : IGameAutomationService
    {
        private readonly IProcessMonitor _monitor;
        private readonly IStopEventsControl? _stopControl;
        private readonly IConfigProvider _configProvider;
        private readonly IHdrController _hdr;
        private readonly IPlayTimeService _playTime;
        private readonly ILogger<GameAutomationService> _logger;
        private readonly object _stateLock = new();

        private readonly GameMatcher _gameMatcher = new();

        private readonly Dictionary<string, List<ActiveProcessEntry>> _activeByName = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, List<ActiveProcessEntry>> _activeByPath = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, int> _dataKeyRefs = new(StringComparer.OrdinalIgnoreCase);

        private IReadOnlyDictionary<string, GameConfig> _configs = new Dictionary<string, GameConfig>(StringComparer.OrdinalIgnoreCase);
        private IReadOnlyDictionary<string, GameConfig> _configsByDataKey = new Dictionary<string, GameConfig>(StringComparer.OrdinalIgnoreCase);
        private IReadOnlyDictionary<string, GameConfig> _configsByPath = new Dictionary<string, GameConfig>(StringComparer.OrdinalIgnoreCase);
        private NameConfigEntry[] _nameConfigs = Array.Empty<NameConfigEntry>();


        public GameAutomationService(
            IProcessMonitor monitor,
            IConfigProvider configProvider,
            IHdrController hdr,
            IPlayTimeService playTime,
            ILogger<GameAutomationService> logger)
        {
            _monitor = monitor;
            _stopControl = monitor as IStopEventsControl;
            _configProvider = configProvider;
            _hdr = hdr;
            _playTime = playTime;
            _logger = logger;
        }

        public void Start()
        {
            lock (_stateLock)
            {
                LoadAndBuildIndexes();

                _activeByName.Clear();
                _activeByPath.Clear();
                _dataKeyRefs.Clear();

                _monitor.ProcessStarted += OnProcessStarted;
                _monitor.ProcessStopped += OnProcessStopped;

                try
                {
                    _stopControl?.SetStopEventsEnabled(false);
                    _logger.LogDebug("Stop events listening disabled at startup");
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Failed to disable stop events at startup");
                }

                _logger.LogInformation(
                    "GameAutomationService started: {Total} configs ({PathCount} path, {NameCount} name)",
                    _configs.Count,
                    _configsByPath.Count,
                    _nameConfigs.Length);
            }
        }

        public void ReloadConfig()
        {
            lock (_stateLock)
            {
                LoadAndBuildIndexes();
                _logger.LogInformation(
                    "GameAutomationService config reloaded: {Total} configs ({PathCount} path, {NameCount} name)",
                    _configs.Count,
                    _configsByPath.Count,
                    _nameConfigs.Length);
            }
        }

        public void Stop()
        {
            lock (_stateLock)
            {
                _monitor.ProcessStarted -= OnProcessStarted;
                _monitor.ProcessStopped -= OnProcessStopped;

                foreach (var dataKey in _dataKeyRefs.Keys.ToList())
                {
                    try
                    {
                        _playTime.StopTracking(dataKey);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Failed to flush session for {DataKey}", dataKey);
                    }
                }

                _dataKeyRefs.Clear();
                _activeByName.Clear();
                _activeByPath.Clear();

                try
                {
                    _stopControl?.SetStopEventsEnabled(false);
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Failed to disable stop events during shutdown");
                }

                _logger.LogInformation("GameAutomationService stopped");
            }
        }

        private void OnProcessStarted(ProcessEventInfo processInfo)
        {
            lock (_stateLock)
            {
                var normalizedPath = NormalizePath(processInfo.ExecutablePath);
                var normalizedName = NormalizeName(processInfo.ExecutableName);

                var config = MatchByPath(normalizedPath);
                string matchLabel = "路径匹配";

                if (config is null)
                {
                    config = MatchByMetadata(processInfo, normalizedPath, out matchLabel);
                }

                if (config is null)
                {
                    _logger.LogDebug("未匹配到配置: {Executable} (Path={Path})", processInfo.ExecutableName, processInfo.ExecutablePath);
                    return;
                }

                if (string.IsNullOrWhiteSpace(config.DataKey))
                {
                    _logger.LogWarning("匹配到缺少 DataKey 的配置，忽略: {Executable}", processInfo.ExecutableName);
                    return;
                }

                var hadAnyActive = _dataKeyRefs.Count > 0;
                var firstForDataKey = RegisterActive(config.DataKey, normalizedName, normalizedPath);

                _logger.LogInformation(
                    "Process start: DataKey={DataKey}, Via={Match}, Executable={Executable}, Path={Path}",
                    config.DataKey,
                    matchLabel,
                    normalizedName ?? processInfo.ExecutableName ?? "n/a",
                    normalizedPath ?? "n/a");

                if (firstForDataKey)
                {
                    try
                    {
                        _playTime.StartTracking(config.DataKey);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Failed to start tracking for {DataKey}", config.DataKey);
                    }
                }

                UpdateHdrState();

                if (!hadAnyActive && _dataKeyRefs.Count > 0)
                {
                    try
                    {
                        _stopControl?.SetStopEventsEnabled(true);
                        _logger.LogDebug("Stop events enabled (first active)");
                    }
                    catch (Exception ex)
                    {
                        _logger.LogDebug(ex, "Failed to enable stop events");
                    }
                }
            }
        }

        private void OnProcessStopped(ProcessEventInfo processInfo)
        {
            lock (_stateLock)
            {
                var hadAnyActive = _dataKeyRefs.Count > 0;

                if (!TryResolveActive(processInfo, out var entry))
                {
                    _logger.LogDebug("Stop ignored, no active record for {Executable}", processInfo.ExecutableName);
                    return;
                }

                var isLastForDataKey = ReleaseActive(entry.DataKey);

                _logger.LogInformation(
                    "Process stop: DataKey={DataKey}, Executable={Executable}, Path={Path}",
                    entry.DataKey,
                    entry.NormalizedName ?? processInfo.ExecutableName ?? "n/a",
                    entry.NormalizedPath ?? processInfo.ExecutablePath ?? "n/a");

                if (isLastForDataKey)
                {
                    try
                    {
                        var session = _playTime.StopTracking(entry.DataKey);
                        if (session is not null)
                        {
                            var formatted = FormatDuration(session.Duration);
                            _logger.LogInformation(
                                "本次游玩时长: {Duration} (开始 {StartTime:t}, 结束 {EndTime:t})",
                                formatted,
                                session.StartTime,
                                session.EndTime);
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Failed to stop tracking for {DataKey}", entry.DataKey);
                    }
                }

                UpdateHdrState();

                if (_dataKeyRefs.Count == 0 && hadAnyActive)
                {
                    try
                    {
                        _stopControl?.SetStopEventsEnabled(false);
                        _logger.LogDebug("Stop events disabled (none active)");
                    }
                    catch (Exception ex)
                    {
                        _logger.LogDebug(ex, "Failed to disable stop events");
                    }
                }
            }
        }

        private void LoadAndBuildIndexes()
        {
            _configs = _configProvider.Load();

            var dataKeyMap = new Dictionary<string, GameConfig>(StringComparer.OrdinalIgnoreCase);

            var pathMap = new Dictionary<string, GameConfig>(StringComparer.OrdinalIgnoreCase);
            var nameStats = new Dictionary<string, (int Count, int MissingPathCount)>(StringComparer.OrdinalIgnoreCase);
            var nameEntries = new List<NameConfigEntry>();

            foreach (var config in _configs.Values)
            {
                if (config is null || !config.IsEnabled)
                {
                    continue;
                }

                if (!string.IsNullOrWhiteSpace(config.DataKey) && !dataKeyMap.TryAdd(config.DataKey, config))
                {
                    _logger.LogWarning(
                        "Duplicate DataKey detected while building indexes: {DataKey}. Keeping the first entry.",
                        config.DataKey);
                }

                var normalizedPath = NormalizePath(config.ExecutablePath);
                if (normalizedPath is not null)
                {
                    if (pathMap.ContainsKey(normalizedPath))
                    {
                        _logger.LogWarning(
                            "Duplicate executable path detected while building indexes: {Path}. Keeping the first entry.",
                            normalizedPath);
                    }
                    else
                    {
                        pathMap[normalizedPath] = config;
                    }
                }

                var normalizedName = NormalizeName(config.ExecutableName);
                if (normalizedName is not null)
                {
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
                    nameEntries.Add(new NameConfigEntry(normalizedName, normalizedName.ToUpperInvariant(), config));
                }
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

            _configsByDataKey = dataKeyMap;
            _configsByPath = pathMap;
            _nameConfigs = nameEntries.ToArray();
        }

        private GameConfig? MatchByPath(string? normalizedPath)
        {
            return _gameMatcher.MatchByPath(normalizedPath, _configsByPath, _logger);
        }

        private GameConfig? MatchByMetadata(ProcessEventInfo processInfo, string? normalizedPath, out string label)
        {
            return _gameMatcher.MatchByMetadata(processInfo, normalizedPath, _nameConfigs, _logger, out label);
        }

        private bool RegisterActive(string dataKey, string? normalizedName, string? normalizedPath)
        {
            var entry = new ActiveProcessEntry(dataKey, normalizedName, normalizedPath);

            if (normalizedName is not null)
            {
                if (!_activeByName.TryGetValue(normalizedName, out var list))
                {
                    list = new List<ActiveProcessEntry>();
                    _activeByName[normalizedName] = list;
                }

                list.Add(entry);
            }

            if (normalizedPath is not null)
            {
                if (!_activeByPath.TryGetValue(normalizedPath, out var list))
                {
                    list = new List<ActiveProcessEntry>();
                    _activeByPath[normalizedPath] = list;
                }

                list.Add(entry);
            }

            if (!_dataKeyRefs.TryGetValue(dataKey, out var count))
            {
                _dataKeyRefs[dataKey] = 1;
                return true;
            }

            _dataKeyRefs[dataKey] = count + 1;
            return false;
        }

        private bool TryResolveActive(ProcessEventInfo processInfo, out ActiveProcessEntry entry)
        {
            var normalizedPath = NormalizePath(processInfo.ExecutablePath);
            if (normalizedPath is not null &&
                _activeByPath.TryGetValue(normalizedPath, out var byPath) &&
                byPath.Count > 0)
            {
                entry = byPath[0];
                RemoveEntry(normalizedPath, entry, byPath, _activeByPath);
                return true;
            }

            var normalizedName = NormalizeName(processInfo.ExecutableName);
            if (normalizedName is not null &&
                _activeByName.TryGetValue(normalizedName, out var byName) &&
                byName.Count > 0)
            {
                entry = byName[0];
                RemoveEntry(normalizedName, entry, byName, _activeByName);
                return true;
            }

            entry = default;
            return false;
        }

        private bool ReleaseActive(string dataKey)
        {
            if (!_dataKeyRefs.TryGetValue(dataKey, out var count))
            {
                return true;
            }

            if (count <= 1)
            {
                _dataKeyRefs.Remove(dataKey);
                return true;
            }

            _dataKeyRefs[dataKey] = count - 1;
            return false;
        }

        private void RemoveEntry(
            string key,
            ActiveProcessEntry entry,
            List<ActiveProcessEntry> list,
            Dictionary<string, List<ActiveProcessEntry>> map)
        {
            list.Remove(entry);
            if (list.Count == 0)
            {
                map.Remove(key);
            }

            if (entry.NormalizedName is not null &&
                _activeByName.TryGetValue(entry.NormalizedName, out var nameList))
            {
                nameList.Remove(entry);
                if (nameList.Count == 0)
                {
                    _activeByName.Remove(entry.NormalizedName);
                }
            }

            if (entry.NormalizedPath is not null &&
                _activeByPath.TryGetValue(entry.NormalizedPath, out var pathList))
            {
                pathList.Remove(entry);
                if (pathList.Count == 0)
                {
                    _activeByPath.Remove(entry.NormalizedPath);
                }
            }
        }

        private void UpdateHdrState()
        {
            var shouldEnableHdr = _dataKeyRefs.Keys.Any(dataKey =>
                _configsByDataKey.TryGetValue(dataKey, out var config) && config.HDREnabled);

            if (shouldEnableHdr && !_hdr.IsEnabled)
            {
                _logger.LogInformation("Enabling HDR (active HDR-enabled game)");
                _hdr.Enable();
            }
            else if (!shouldEnableHdr && _hdr.IsEnabled)
            {
                _logger.LogInformation("Disabling HDR (no HDR-enabled game remaining)");
                _hdr.Disable();
            }
        }

        private static string? NormalizeName(string? executableName)
        {
            return PathNormalizer.NormalizeName(executableName);
        }

        private static string? NormalizePath(string? executablePath)
        {
            return PathNormalizer.NormalizePath(executablePath);
        }

        private string? TryGetProductName(string? executablePath)
        {
            return _gameMatcher.TryGetProductName(executablePath, _logger);
        }

        private bool IsSystemPath(string processPath)
        {
            return _gameMatcher.IsSystemPath(processPath, _logger);
        }

        private bool IsPathRelated(string processPath, string? configPath)
        {
            return _gameMatcher.IsPathRelated(processPath, configPath, _logger);
        }

        private static bool TryResolveWindowsPath(string path, out string normalizedPath, out string directory)
        {
            return PathNormalizer.TryResolveWindowsPath(path, out normalizedPath, out directory);
        }

        private static string EnsureTrailingSeparator(string path, bool preferBackslash = false)
        {
            return PathNormalizer.EnsureTrailingSeparator(path, preferBackslash);
        }

        /// <summary>
        /// 根据可执行文件名长度计算模糊匹配阈值。
        /// 短文件名需要更高相似度以避免误匹配。
        /// </summary>
        /// <param name="executableName">可执行文件名（可含扩展名）</param>
        /// <returns>推荐模糊匹配阈值（80-95）</returns>
        private static int CalculateFuzzyThreshold(string executableName)
        {
            return GameMatcher.CalculateFuzzyThreshold(executableName);
        }

        private static string FormatDuration(TimeSpan duration)
        {
            return TimeFormatting.FormatDuration(duration);
        }

        private readonly record struct ActiveProcessEntry(string DataKey, string? NormalizedName, string? NormalizedPath);
    }
}



