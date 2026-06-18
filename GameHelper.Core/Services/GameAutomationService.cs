using System;
using System.Collections.Generic;
using System.Linq;
using GameHelper.Core.Abstractions;
using GameHelper.Core.Models;
using GameHelper.Core.Utilities;
using Microsoft.Extensions.Logging;

namespace GameHelper.Core.Services;

/// <summary>
/// Coordinates process monitoring, playtime tracking, and HDR toggling based on enabled games.
/// Lifecycle: call <see cref="Start"/> to subscribe to monitor events and load config,
/// and <see cref="Stop"/> to unsubscribe and flush any active sessions.
/// </summary>
public sealed class GameAutomationService : IGameAutomationService
{
    private readonly IProcessMonitor _monitor;
    private readonly IStopEventsControl? _stopControl;
    private readonly IProcessNameFilterControl? _nameFilterControl;
    private readonly IConfigProvider _configProvider;
    private readonly IHdrController _hdr;
    private readonly IProcessPathResolver? _pathResolver;
    private readonly HdrScheduler _hdrScheduler = new();
    private readonly IPlayTimeService _playTime;
    private readonly ILogger<GameAutomationService> _logger;
    private readonly object _stateLock = new();

    private readonly SessionTracker _sessionTracker = new();

    private AutomationConfigIndex _configIndex = AutomationConfigIndex.Empty;

    public GameAutomationService(
        IProcessMonitor monitor,
        IConfigProvider configProvider,
        IHdrController hdr,
        IPlayTimeService playTime,
        ILogger<GameAutomationService> logger,
        IProcessPathResolver? pathResolver = null)
    {
        _monitor = monitor;
        _stopControl = monitor as IStopEventsControl;
        _nameFilterControl = monitor as IProcessNameFilterControl;
        _configProvider = configProvider;
        _hdr = hdr;
        _pathResolver = pathResolver;
        _playTime = playTime;
        _logger = logger;
    }

    public void Start()
    {
        lock (_stateLock)
        {
            LoadAndBuildIndexes();
            SyncProcessNameFilter();

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
                "GameAutomationService started: {Total} configs ({PathCount} path, {ExactNameCount} exact-name, {NameCount} fuzzy-name)",
                _configIndex.All.Count,
                _configIndex.ByPath.Count,
                _configIndex.ByExactName.Count,
                _configIndex.ByName.Length);
        }
    }

    public void ReloadConfig()
    {
        lock (_stateLock)
        {
            LoadAndBuildIndexes();
            SyncProcessNameFilter();
            _logger.LogInformation(
                "GameAutomationService config reloaded: {Total} configs ({PathCount} path, {ExactNameCount} exact-name, {NameCount} fuzzy-name)",
                _configIndex.All.Count,
                _configIndex.ByPath.Count,
                _configIndex.ByExactName.Count,
                _configIndex.ByName.Length);
        }
    }

    public void Stop()
    {
        lock (_stateLock)
        {
            _monitor.ProcessStarted -= OnProcessStarted;
            _monitor.ProcessStopped -= OnProcessStopped;

            foreach (var dataKey in _sessionTracker.GetActiveDataKeysSnapshot())
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
            var normalizedName = PathNormalizer.NormalizeName(processInfo.ExecutableName);
            var config = MatchProcessStart(processInfo, normalizedName, out var normalizedPath, out var matchLabel);

            if (config is null)
            {
                _logger.LogDebug(
                    "No game config matched: {Executable} (Path={Path}, PID={ProcessId})",
                    processInfo.ExecutableName,
                    processInfo.ExecutablePath,
                    processInfo.ProcessId);
                return;
            }

            if (string.IsNullOrWhiteSpace(config.DataKey))
            {
                _logger.LogWarning("Matched config without DataKey, ignoring: {Executable}", processInfo.ExecutableName);
                return;
            }

            var hadAnyActive = _sessionTracker.ActiveCount > 0;
            var firstForDataKey = _sessionTracker.Register(
                config.DataKey,
                normalizedName,
                normalizedPath,
                processInfo.ProcessId);

            _logger.LogInformation(
                "Process start: DataKey={DataKey}, Via={Match}, Executable={Executable}, Path={Path}, PID={ProcessId}",
                config.DataKey,
                matchLabel,
                normalizedName ?? processInfo.ExecutableName ?? "n/a",
                normalizedPath ?? "n/a",
                processInfo.ProcessId);

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

            SyncProcessNameFilter();
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
                _logger.LogDebug(
                    "Stop ignored, no active record for {Executable} (Path={Path}, PID={ProcessId})",
                    processInfo.ExecutableName,
                    processInfo.ExecutablePath,
                    processInfo.ProcessId);
                return;
            }

            var isLastForDataKey = _sessionTracker.Release(entry.DataKey);
            SyncProcessNameFilter();

            _logger.LogInformation(
                "Process stop: DataKey={DataKey}, Executable={Executable}, Path={Path}, PID={ProcessId}",
                entry.DataKey,
                entry.NormalizedName ?? processInfo.ExecutableName ?? "n/a",
                entry.NormalizedPath ?? processInfo.ExecutablePath ?? "n/a",
                entry.ProcessId ?? processInfo.ProcessId);

            if (isLastForDataKey)
            {
                try
                {
                    var session = _playTime.StopTracking(entry.DataKey);
                    if (session is not null)
                    {
                        var formatted = TimeFormatting.FormatDuration(session.Duration);
                        _logger.LogInformation(
                            "Session duration: {Duration} (Start {StartTime:t}, End {EndTime:t})",
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

    private GameConfig? MatchProcessStart(
        ProcessEventInfo processInfo,
        string? normalizedName,
        out string? normalizedPath,
        out string matchLabel)
    {
        normalizedPath = null;
        matchLabel = "not matched";

        var pathHint = PathNormalizer.NormalizePath(processInfo.ExecutablePath);
        var pathHintConfig = GameMatcher.MatchByPath(pathHint, _configIndex.ByPath, _logger);
        if (pathHintConfig is not null)
        {
            normalizedPath = pathHint;
            matchLabel = "path-hint";
            return pathHintConfig;
        }

        // Only name-gated monitors may resolve PID before the candidate-name gate.
        // Monitors that do not implement the filter can emit every system process start here.
        var resolvedPath = _nameFilterControl is not null
            ? ResolvePathByPid(processInfo.ProcessId)
            : null;
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

        return PathNormalizer.NormalizePath(processInfo.ExecutablePath);
    }

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

    private void LoadAndBuildIndexes()
    {
        _configIndex = AutomationConfigIndex.Build(_configProvider.Load(), _logger);
    }

    private void SyncProcessNameFilter()
    {
        try
        {
            var allowedNames = _configIndex.ByExactName.Keys
                .Concat(_sessionTracker.GetActiveNamesSnapshot())
                .Distinct(StringComparer.OrdinalIgnoreCase);

            // Config reloads can remove a game while its process is still active.
            // Keep active names in the ETW gate until the matching stop event is observed.
            _nameFilterControl?.SetAllowedProcessNames(allowedNames);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to update process-name filter");
        }
    }

    private void UpdateHdrState()
    {
        _hdrScheduler.Update(_sessionTracker.GetActiveDataKeysSnapshot(), _configIndex.ByDataKey, _hdr, _logger);
    }
}
