using System;
using System.Collections.Generic;
using System.Linq;
using GameHelper.Core.Abstractions;
using GameHelper.Core.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

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
        private readonly IGameProcessMatcher _gameProcessMatcher;
        private readonly IHdrController _hdr;
        private readonly IPlayTimeService _playTime;
        private readonly ILogger<GameAutomationService> _logger;
        private readonly object _stateLock = new();

        private readonly Dictionary<string, List<ActiveProcessEntry>> _activeByName = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, List<ActiveProcessEntry>> _activeByPath = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, int> _dataKeyRefs = new(StringComparer.OrdinalIgnoreCase);

        private GameProcessMatchSnapshot _snapshot = new();

        public GameAutomationService(
            IProcessMonitor monitor,
            IConfigProvider configProvider,
            IGameProcessMatcher gameProcessMatcher,
            IHdrController hdr,
            IPlayTimeService playTime,
            ILogger<GameAutomationService> logger)
        {
            _monitor = monitor;
            _stopControl = monitor as IStopEventsControl;
            _configProvider = configProvider;
            _gameProcessMatcher = gameProcessMatcher;
            _hdr = hdr;
            _playTime = playTime;
            _logger = logger;
        }

        public GameAutomationService(
            IProcessMonitor monitor,
            IConfigProvider configProvider,
            IHdrController hdr,
            IPlayTimeService playTime,
            ILogger<GameAutomationService> logger)
            : this(monitor, configProvider, new GameProcessMatcher(NullLogger<GameProcessMatcher>.Instance), hdr, playTime, logger)
        {
        }

        public void Start()
        {
            lock (_stateLock)
            {
                LoadSnapshot();

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
                    _snapshot.Configs.Count,
                    _snapshot.ConfigsByPath.Count,
                    _snapshot.NameEntries.Count);
            }
        }

        public void ReloadConfig()
        {
            lock (_stateLock)
            {
                LoadSnapshot();
                _logger.LogInformation(
                    "GameAutomationService config reloaded: {Total} configs ({PathCount} path, {NameCount} name)",
                    _snapshot.Configs.Count,
                    _snapshot.ConfigsByPath.Count,
                    _snapshot.NameEntries.Count);
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

        private void LoadSnapshot()
        {
            _snapshot = _gameProcessMatcher.CreateSnapshot(_configProvider.Load());
        }

        private void OnProcessStarted(ProcessEventInfo processInfo)
        {
            lock (_stateLock)
            {
                var normalizedPath = _gameProcessMatcher.NormalizePath(processInfo.ExecutablePath);
                var normalizedName = _gameProcessMatcher.NormalizeName(processInfo.ExecutableName);
                var match = _gameProcessMatcher.Match(processInfo, _snapshot);

                if (match is null)
                {
                    _logger.LogDebug("未匹配到配置: {Executable} (Path={Path})", processInfo.ExecutableName, processInfo.ExecutablePath);
                    return;
                }

                if (string.IsNullOrWhiteSpace(match.Config.DataKey))
                {
                    _logger.LogWarning("匹配到缺少 DataKey 的配置，忽略: {Executable}", processInfo.ExecutableName);
                    return;
                }

                var hadAnyActive = _dataKeyRefs.Count > 0;
                var firstForDataKey = RegisterActive(match.Config.DataKey, normalizedName, normalizedPath);

                _logger.LogInformation(
                    "Process start: DataKey={DataKey}, Via={Match}, Executable={Executable}, Path={Path}",
                    match.Config.DataKey,
                    match.MatchLabel,
                    normalizedName ?? processInfo.ExecutableName ?? "n/a",
                    normalizedPath ?? "n/a");

                if (firstForDataKey)
                {
                    try
                    {
                        _playTime.StartTracking(match.Config.DataKey);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Failed to start tracking for {DataKey}", match.Config.DataKey);
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
            var normalizedPath = _gameProcessMatcher.NormalizePath(processInfo.ExecutablePath);
            if (normalizedPath is not null &&
                _activeByPath.TryGetValue(normalizedPath, out var byPath) &&
                byPath.Count > 0)
            {
                entry = byPath[0];
                RemoveEntry(normalizedPath, entry);
                return true;
            }

            var normalizedName = _gameProcessMatcher.NormalizeName(processInfo.ExecutableName);
            if (normalizedName is not null &&
                _activeByName.TryGetValue(normalizedName, out var byName) &&
                byName.Count > 0)
            {
                entry = byName[0];
                RemoveEntry(normalizedName, entry);
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

        private void RemoveEntry(string lookupKey, ActiveProcessEntry entry)
        {
            if (_activeByName.TryGetValue(entry.NormalizedName ?? lookupKey, out var nameList))
            {
                nameList.Remove(entry);
                if (nameList.Count == 0)
                {
                    _activeByName.Remove(entry.NormalizedName ?? lookupKey);
                }
            }

            if (_activeByPath.TryGetValue(entry.NormalizedPath ?? lookupKey, out var pathList))
            {
                pathList.Remove(entry);
                if (pathList.Count == 0)
                {
                    _activeByPath.Remove(entry.NormalizedPath ?? lookupKey);
                }
            }
        }

        private void UpdateHdrState()
        {
            var shouldEnableHdr = _dataKeyRefs.Keys.Any(dataKey =>
                _snapshot.ConfigsByDataKey.TryGetValue(dataKey, out var config) && config.HDREnabled);

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

        private static string FormatDuration(TimeSpan duration)
        {
            if (duration < TimeSpan.Zero)
            {
                duration = TimeSpan.Zero;
            }

            if (duration == TimeSpan.Zero)
            {
                return "0秒";
            }

            var parts = new List<string>();
            if (duration.Days > 0)
            {
                parts.Add($"{duration.Days}天");
            }

            if (duration.Hours > 0)
            {
                parts.Add($"{duration.Hours}小时");
            }

            if (duration.Minutes > 0)
            {
                parts.Add($"{duration.Minutes}分钟");
            }

            var seconds = duration.Seconds;
            if (seconds > 0 || parts.Count == 0)
            {
                parts.Add($"{seconds}秒");
            }

            return string.Concat(parts);
        }

        private readonly record struct ActiveProcessEntry(string DataKey, string? NormalizedName, string? NormalizedPath);
    }
}
