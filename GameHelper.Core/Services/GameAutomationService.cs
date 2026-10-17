using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
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
    private readonly IGameConfiguration _gameConfiguration;
    private readonly IHdrController _hdr;
    private readonly IProcessPathResolver? _pathResolver;
    private readonly HdrScheduler _hdrScheduler = new();
    private readonly IPlayTimeService _playTime;
    private readonly IGameSpeedController? _speedController;
    private readonly IBreakReminderService? _breakReminder;
    private readonly IMonitorModeController? _monitorModeController;
    private readonly IInFlightSessionStore? _inFlightSessionStore;
    private readonly IProcessIdentityProbe? _processIdentityProbe;
    private readonly IUserLocationSource? _locationSource;
    private readonly ILocationCacheStore? _locationCacheStore;
    private readonly MonitorModeScheduler _monitorModeScheduler = new();
    private MonitorModePlan? _monitorModePlan;
    private UserCoordinate? _monitorModeCoordinates;
    private Timer? _monitorModeTimer;
    private bool? _lastMonitorModeAnyGameActive;
    private bool? _lastMonitorModeDaylight;
    private readonly Dictionary<string, DateTime> _inFlightStarts = new(StringComparer.OrdinalIgnoreCase);
    private readonly ILogger<GameAutomationService> _logger;
    private readonly object _stateLock = new();

    private readonly SessionTracker _sessionTracker = new();

    private AutomationConfigIndex _configIndex = AutomationConfigIndex.Empty;
    private GameProcessMatcher _processMatcher;
    private bool _started;

    public GameAutomationService(
        IProcessMonitor monitor,
        IGameConfiguration gameConfiguration,
        IHdrController hdr,
        IPlayTimeService playTime,
        ILogger<GameAutomationService> logger,
        IProcessPathResolver? pathResolver = null,
        IGameSpeedController? speedController = null,
        IBreakReminderService? breakReminder = null,
        IMonitorModeController? monitorModeController = null,
        IInFlightSessionStore? inFlightSessionStore = null,
        IProcessIdentityProbe? processIdentityProbe = null,
        IUserLocationSource? locationSource = null,
        ILocationCacheStore? locationCacheStore = null)
    {
        _monitor = monitor;
        _gameConfiguration = gameConfiguration;
        _hdr = hdr;
        _pathResolver = pathResolver;
        _playTime = playTime;
        _speedController = speedController;
        _breakReminder = breakReminder;
        _monitorModeController = monitorModeController;
        _inFlightSessionStore = inFlightSessionStore;
        _processIdentityProbe = processIdentityProbe;
        _locationSource = locationSource;
        _locationCacheStore = locationCacheStore;
        _logger = logger;
        _processMatcher = new GameProcessMatcher(_configIndex, _pathResolver, _logger);
    }

    public void Start()
    {
        lock (_stateLock)
        {
            if (_started) return;
            var configuration = LoadAndBuildIndexes();
            _sessionTracker.Clear();
            RecoverInFlightSessions();
            ConfigureProcessObservation();

            _monitor.ProcessStarted += OnProcessStarted;
            _monitor.ProcessStopped += OnProcessStopped;
            _started = true;
            try { _breakReminder?.Start(configuration); }
            catch (Exception ex) { _logger.LogWarning(ex, "Failed to start break reminders"); }

            if (_monitorModeController is not null)
            {
                try
                {
                    _monitorModeTimer = new Timer(OnMonitorModeTick, null, TimeSpan.FromSeconds(15), TimeSpan.FromMinutes(1));
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to start the monitor mode timer; preset switching will only follow game events.");
                }
            }

            if (_monitorModePlan is not null && NeedsAutoLocation(configuration.MonitorMode) && _locationCacheStore is not null)
            {
                try
                {
                    _ = Task.Run(() => BootstrapMonitorModeLocation(configuration.MonitorMode!));
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to kick off the monitor mode location bootstrap; the daylight window falls back to fixed times.");
                }
            }

            _logger.LogInformation(
                "GameAutomationService started: {Total} configs ({PathCount} path, {ExactNameCount} exact-name, {NameCount} fuzzy-name)",
                _configIndex.All.Count,
                _configIndex.ByPath.Count,
                _configIndex.ByExactName.Count,
                _configIndex.ByName.Length);
        }

        // Evaluate the preset rule right after startup so the monitor lands in the
        // expected mode instead of waiting for the first game event.
        if (_monitorModeController is not null)
        {
            UpdateMonitorModeState(forced: true);
        }
    }

    public void ReloadConfig()
    {
        lock (_stateLock)
        {
            var configuration = LoadAndBuildIndexes();
            ConfigureProcessObservation();
            _breakReminder?.ReloadConfig(configuration);
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
            if (_monitorModeTimer is not null)
            {
                try { _monitorModeTimer.Dispose(); }
                catch (Exception ex) { _logger.LogDebug(ex, "Failed to dispose the monitor mode timer"); }
                _monitorModeTimer = null;
                _lastMonitorModeAnyGameActive = null;
                _lastMonitorModeDaylight = null;
            }

            try { _breakReminder?.Stop(); }
            catch (Exception ex) { _logger.LogWarning(ex, "Failed to stop break reminders"); }
            _started = false;
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
            _inFlightStarts.Clear();

            try
            {
                _inFlightSessionStore?.Clear();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to clear the in-flight session snapshot after stopping");
            }

            try
            {
                ConfigureProcessObservation();
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

                ApplyConfiguredSpeed(config, processInfo.ProcessId);

                // Persist the in-flight snapshot so a restart (deploy or crash) can
                // resume this session instead of losing it.
                _inFlightStarts[config.DataKey] = DateTime.Now;
                PersistInFlightSnapshot();
            }

            ConfigureProcessObservation();
            UpdateHdrState();

            if (!hadAnyActive && _sessionTracker.ActiveCount > 0)
            {
                _logger.LogDebug("Stop events enabled (first active)");
            }
        }

        // Preset switching performs DDC/CI bus I/O; keep it outside the state lock.
        UpdateMonitorModeState(forced: true);
    }

    private void ApplyConfiguredSpeed(GameConfig config, int? processId)
    {
        if (!config.SpeedMultiplier.HasValue || _speedController is null)
        {
            return;
        }

        if (!processId.HasValue)
        {
            _logger.LogWarning(
                "Cannot apply configured speed for {DataKey}: process ID is unavailable",
                config.DataKey);
            return;
        }

        try
        {
            _speedController.ApplySpeed(processId.Value, config.SpeedMultiplier.Value);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to apply configured speed for {DataKey}", config.DataKey);
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
            ConfigureProcessObservation();

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

            if (isLastForDataKey)
            {
                _inFlightStarts.Remove(entry.DataKey);
                PersistInFlightSnapshot();
            }

            UpdateHdrState();

            if (_sessionTracker.ActiveCount == 0 && hadAnyActive)
            {
                _logger.LogDebug("Stop events disabled (none active)");
            }
        }

        // Preset switching performs DDC/CI bus I/O; keep it outside the state lock.
        UpdateMonitorModeState(forced: true);
    }

    private GameConfig? MatchProcessStart(
        ProcessEventInfo processInfo,
        string? normalizedName,
        out string? normalizedPath,
        out string matchLabel) =>
        _processMatcher.Match(processInfo, normalizedName, out normalizedPath, out matchLabel);

    private AppConfig LoadAndBuildIndexes()
    {
        var configuration = _gameConfiguration.Read();
        _monitorModePlan = MonitorModePlan.TryCreate(configuration.MonitorMode, _logger, ResolveMonitorModeCoordinates);
        var configs = (configuration.Games ?? new List<GameConfig>())
            .Where(static config => !string.IsNullOrWhiteSpace(config.DataKey))
            .ToDictionary(static config => config.DataKey, StringComparer.OrdinalIgnoreCase);
        _configIndex = AutomationConfigIndex.Build(configs, _logger);
        _processMatcher = new GameProcessMatcher(_configIndex, _pathResolver, _logger);
        return configuration;
    }

    private void ConfigureProcessObservation()
    {
        var candidateNames = _configIndex.ByExactName.Keys
            .Concat(_sessionTracker.GetActiveNamesSnapshot())
            .Distinct(StringComparer.OrdinalIgnoreCase);

        // Config reloads can remove a game while its process is still active. Keep active names
        // in the gate until their stop event is observed, and only observe stops while needed.
        _monitor.Configure(new ProcessObservationPolicy(
            candidateNames,
            observeStopEvents: _sessionTracker.ActiveCount > 0));
    }

    private void UpdateHdrState()
    {
        _hdrScheduler.Update(_sessionTracker.GetActiveDataKeysSnapshot(), _configIndex.ByDataKey, _hdr, _logger);
    }

    private void OnMonitorModeTick(object? state)
    {
        UpdateMonitorModeState(forced: false);
    }

    /// <summary>
    /// Re-evaluates the monitor preset rule. Gating happens under the state lock, while
    /// the DDC/CI bus I/O inside <see cref="MonitorModeScheduler.Update"/> stays outside
    /// it so a slow monitor never stalls session bookkeeping.
    /// </summary>
    private void UpdateMonitorModeState(bool forced)
    {
        var controller = _monitorModeController;
        if (controller is null)
        {
            return;
        }

        MonitorModePlan? plan;
        var anyGameActive = false;
        lock (_stateLock)
        {
            plan = _monitorModePlan;
            if (plan is null)
            {
                return;
            }

            anyGameActive = _sessionTracker.ActiveCount > 0;
            var daylight = plan.Daylight.IsDaylight(DateTimeOffset.Now);

            if (!forced
                && _lastMonitorModeAnyGameActive == anyGameActive
                && _lastMonitorModeDaylight == daylight)
            {
                return;
            }

            _lastMonitorModeAnyGameActive = anyGameActive;
            _lastMonitorModeDaylight = daylight;
        }

        try
        {
            _monitorModeScheduler.Update(plan, anyGameActive, controller, _logger, DateTimeOffset.Now);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Monitor mode update failed; the next event or tick will retry.");
            lock (_stateLock)
            {
                // Invalidate the change gate so subsequent ticks retry even without a state change.
                _lastMonitorModeDaylight = null;
            }
        }
    }

    /// <summary>
    /// Persists the current in-flight session snapshot. Callers must hold or be inside
    /// the state lock so the tracker snapshot is consistent.
    /// </summary>
    private void PersistInFlightSnapshot()
    {
        var store = _inFlightSessionStore;
        if (store is null)
        {
            return;
        }

        try
        {
            store.Save(BuildInFlightSnapshot());
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to persist the in-flight session snapshot; restart recovery may be incomplete.");
        }
    }

    private InFlightSnapshot BuildInFlightSnapshot()
    {
        var snapshot = new InFlightSnapshot { SnapshotAt = DateTime.Now, Sessions = new List<InFlightSession>() };
        foreach (var entry in _sessionTracker.GetActiveEntriesSnapshot())
        {
            snapshot.Sessions.Add(new InFlightSession
            {
                DataKey = entry.DataKey,
                ProcessId = entry.ProcessId,
                NormalizedName = entry.NormalizedName,
                NormalizedPath = entry.NormalizedPath,
                StartedAt = _inFlightStarts.TryGetValue(entry.DataKey, out var startedAt) ? startedAt : snapshot.SnapshotAt
            });
        }

        return snapshot;
    }

    /// <summary>
    /// Adopts in-flight sessions persisted before a restart: entries whose process is
    /// still the same executable resume timing with the original start time; sessions
    /// whose game exited during the downtime are written as interrupted rows so no
    /// playtime is lost. Runs once during <see cref="Start"/>, under the state lock.
    /// </summary>
    private void RecoverInFlightSessions()
    {
        var store = _inFlightSessionStore;
        if (store is null)
        {
            return;
        }

        InFlightSnapshot? snapshot;
        try
        {
            snapshot = store.Load();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to read the in-flight session snapshot; recovery skipped.");
            return;
        }

        if (snapshot is null || snapshot.Sessions.Count == 0)
        {
            return;
        }

        var resumedCount = 0;
        var closedCount = 0;
        foreach (var group in snapshot.Sessions.GroupBy(session => session.DataKey, StringComparer.OrdinalIgnoreCase))
        {
            var liveSessions = new List<InFlightSession>();
            foreach (var session in group)
            {
                if (session.ProcessId.HasValue && IsPersistedProcessStillSame(session))
                {
                    _sessionTracker.Register(session.DataKey, session.NormalizedName, session.NormalizedPath, session.ProcessId);
                    liveSessions.Add(session);
                }
                else
                {
                    _logger.LogInformation(
                        "In-flight session process for {DataKey} did not survive the restart (exited or identity changed).",
                        session.DataKey);
                }
            }

            if (liveSessions.Count > 0)
            {
                var startedAt = liveSessions[0].StartedAt;
                try
                {
                    _playTime.ResumeTracking(group.Key, startedAt);
                    _inFlightStarts[group.Key] = startedAt;
                    resumedCount++;
                    _logger.LogInformation(
                        "Resumed in-flight session for {DataKey}: start {StartTime:t}, {ProcessCount} live process(es).",
                        group.Key,
                        startedAt,
                        liveSessions.Count);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to resume in-flight session for {DataKey}.", group.Key);
                }
            }
            else
            {
                var startedAt = group.First().StartedAt;
                var endTime = snapshot.SnapshotAt;
                // When GameHelper stayed down for a long time the true exit time is
                // unknowable; cap the recovered duration so a stale snapshot cannot
                // inflate playtime unboundedly.
                if (endTime - startedAt > MaxRecoveredSessionDuration)
                {
                    _logger.LogWarning(
                        "In-flight session for {DataKey} spans {Span}; capping the recovered duration at {Cap}.",
                        group.Key,
                        endTime - startedAt,
                        MaxRecoveredSessionDuration);
                    endTime = startedAt + MaxRecoveredSessionDuration;
                }

                try
                {
                    _playTime.WriteRecoveredSession(group.Key, startedAt, endTime);
                    closedCount++;
                    _logger.LogInformation(
                        "Closed interrupted session for {DataKey}: start {StartTime:t}, end {EndTime:t}.",
                        group.Key,
                        startedAt,
                        endTime);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to persist interrupted session for {DataKey}.", group.Key);
                }
            }
        }

        try
        {
            store.Clear();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to clear the in-flight session snapshot after recovery.");
        }

        _logger.LogInformation(
            "In-flight session recovery finished: {Resumed} session(s) resumed, {Closed} session(s) closed.",
            resumedCount,
            closedCount);
    }

    private static bool NeedsAutoLocation(MonitorModeSettings? settings) =>
        settings is not null
        && string.Equals(settings.Location, "auto", StringComparison.OrdinalIgnoreCase)
        && !(settings.Latitude.HasValue && settings.Longitude.HasValue);

    /// <summary>
    /// Coordinates for the solar day window when not configured manually. Called from
    /// daylight evaluations (all of which run inside the state lock); memoizes the
    /// auto-detected coordinate from the cache on first use.
    /// </summary>
    private UserCoordinate? ResolveMonitorModeCoordinates()
    {
        if (_monitorModeCoordinates.HasValue)
        {
            return _monitorModeCoordinates;
        }

        var cache = _locationCacheStore;
        if (cache is null || _monitorModePlan is null)
        {
            return null;
        }

        var cached = cache.Load();
        if (cached is not null)
        {
            var coordinate = new UserCoordinate(cached.Latitude, cached.Longitude);
            _monitorModeCoordinates = coordinate;
            return coordinate;
        }

        return null;
    }

    /// <summary>
    /// Coordinate bootstrap for monitorMode location=auto: prefer a cache younger than a
    /// day, otherwise query the location source once and persist the result. On success
    /// the monitor-mode plan is rebuilt and evaluated immediately so the first rule
    /// application uses the solar window; on failure the fixed time window applies.
    /// </summary>
    private void BootstrapMonitorModeLocation(MonitorModeSettings settings)
    {
        var cache = _locationCacheStore;
        var source = _locationSource;
        if (cache is null)
        {
            return;
        }

        var cached = cache.Load();
        var stale = cached is null || (DateTime.Now - cached.UpdatedAt) > LocationCacheMaxAge;

        UserCoordinate? live = null;
        if (stale && source is not null)
        {
            try
            {
                live = source.TryGetLocation();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Automatic location failed during the monitor mode location bootstrap.");
            }
        }

        UserCoordinate publish;
        if (live is not null)
        {
            try
            {
                cache.Save(live.Value);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to persist the detected location; it will be re-detected next time.");
            }

            publish = live.Value;
        }
        else if (cached is not null)
        {
            if (stale)
            {
                _logger.LogInformation(
                    "monitorMode location=auto could not refresh coordinates (cache age {Age}); using the cached coordinate.",
                    DateTime.Now - cached.UpdatedAt);
            }

            publish = new UserCoordinate(cached.Latitude, cached.Longitude);
        }
        else
        {
            _logger.LogInformation(
                "monitorMode location=auto obtained no coordinates (the system 'Location' switch is off or no fix); the daylight window falls back to daylightStart/daylightEnd, or set latitude/longitude manually.");
            return;
        }

        lock (_stateLock)
        {
            _monitorModeCoordinates = publish;
        }

        try
        {
            ReloadConfig();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to rebuild the monitor mode plan after acquiring coordinates.");
            return;
        }

        lock (_stateLock)
        {
            if (!_started)
            {
                return;
            }
        }

        UpdateMonitorModeState(forced: true);
    }

    private static readonly TimeSpan LocationCacheMaxAge = TimeSpan.FromHours(24);

    private bool IsPersistedProcessStillSame(InFlightSession session)
    {
        var probe = _processIdentityProbe;
        if (probe is null || !session.ProcessId.HasValue)
        {
            return false;
        }

        if (!probe.TryGetImagePath(session.ProcessId.Value, out var imagePath) || string.IsNullOrWhiteSpace(imagePath))
        {
            return false;
        }

        var normalizedImage = PathNormalizer.NormalizePath(imagePath);
        if (session.NormalizedPath is not null && normalizedImage is not null)
        {
            return string.Equals(session.NormalizedPath, normalizedImage, StringComparison.OrdinalIgnoreCase);
        }

        var imageName = PathNormalizer.NormalizeName(imagePath);
        return session.NormalizedName is not null
            && imageName is not null
            && string.Equals(session.NormalizedName, imageName, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Upper bound for a recovered interrupted session: the true exit time of a game that
    /// died while GameHelper was down is unknowable, so the last-known-alive timestamp is
    /// used and pathological spans are capped.
    /// </summary>
    private static readonly TimeSpan MaxRecoveredSessionDuration = TimeSpan.FromHours(12);
}
