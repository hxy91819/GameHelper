using System;
using System.Collections.Generic;
using System.Linq;
using GameHelper.Core.Abstractions;
using GameHelper.Core.Models;
using GameHelper.Core.Utilities;
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
        private readonly HdrScheduler _hdrScheduler = new();
        private readonly IPlayTimeService _playTime;
        private readonly ILogger<GameAutomationService> _logger;
        private readonly object _stateLock = new();

        private readonly GameMatcher _gameMatcher = new();
        private readonly SessionTracker _sessionTracker = new();

        private AutomationConfigIndex _configIndex = AutomationConfigIndex.Empty;


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

                _sessionTracker.Clear();

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
                    _configIndex.All.Count,
                    _configIndex.ByPath.Count,
                    _configIndex.ByName.Length);
            }
        }

        public void ReloadConfig()
        {
            lock (_stateLock)
            {
                LoadAndBuildIndexes();
                _logger.LogInformation(
                    "GameAutomationService config reloaded: {Total} configs ({PathCount} path, {NameCount} name)",
                    _configIndex.All.Count,
                    _configIndex.ByPath.Count,
                    _configIndex.ByName.Length);
            }
        }

        public void Stop()
        {
            lock (_stateLock)
            {
                _monitor.ProcessStarted -= OnProcessStarted;
                _monitor.ProcessStopped -= OnProcessStopped;

                foreach (var dataKey in _sessionTracker.ActiveDataKeys)
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

                _sessionTracker.Clear();

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
                var normalizedPath = PathNormalizer.NormalizePath(processInfo.ExecutablePath);
                var normalizedName = PathNormalizer.NormalizeName(processInfo.ExecutableName);

                var config = _gameMatcher.MatchByPath(normalizedPath, _configIndex.ByPath, _logger);
                string matchLabel = "路径匹配";

                if (config is null)
                {
                    config = _gameMatcher.MatchByMetadata(processInfo, normalizedPath, _configIndex.ByName, _logger, out matchLabel);
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

                var hadAnyActive = _sessionTracker.ActiveCount > 0;
                var firstForDataKey = _sessionTracker.Register(config.DataKey, normalizedName, normalizedPath);

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

                if (!hadAnyActive && _sessionTracker.ActiveCount > 0)
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
                var hadAnyActive = _sessionTracker.ActiveCount > 0;

                if (!_sessionTracker.TryResolve(processInfo, out var entry))
                {
                    _logger.LogWarning("Stop ignored, no active record for {Executable} (Path={Path})", processInfo.ExecutableName, processInfo.ExecutablePath);
                    return;
                }

                var isLastForDataKey = _sessionTracker.Release(entry.DataKey);

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
                            var formatted = TimeFormatting.FormatDuration(session.Duration);
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

                if (_sessionTracker.ActiveCount == 0 && hadAnyActive)
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
            _configIndex = AutomationConfigIndex.Build(_configProvider.Load(), _logger);
        }

        private void UpdateHdrState()
        {
            _hdrScheduler.Update(_sessionTracker.ActiveDataKeys, _configIndex.ByDataKey, _hdr, _logger);
        }
    }
}



