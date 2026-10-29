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
public sealed class GameAutomationService : IGameAutomationService, IGameLaunchCoordinator
{
    private readonly IProcessMonitor _monitor;
    private readonly IGameConfiguration _gameConfiguration;
    private readonly IHdrController _hdr;
    private readonly IGameLauncher? _gameLauncher;
    private readonly Ds4WindowsAutomationPolicy _companionPolicy;
    private readonly AfterExitActionQueue _afterExitActions;
    private readonly IProcessPathResolver? _pathResolver;
    private readonly IPlayTimeService _playTime;
    private readonly IBreakReminderService? _breakReminder;
    private readonly TimeProvider _timeProvider;
    private readonly SpeedAttachmentCoordinator _speedAttachment;
    private readonly MonitorModeCoordinator _monitorMode;
    private readonly LaunchOwnershipCoordinator _launchOwnership;
    private readonly InFlightSessionRecovery _inFlightRecovery;
    private ITimer? _maintenanceTimer;
    private readonly ILogger<GameAutomationService> _logger;
    private readonly object _stateLock = new();
    // Global lock order: monitor update → companion/before-launch execution → state.
    // Queued after-exit admission borrows only state; its I/O and bounded drain hold no locks.
    // Only short session transactions stay under state, with one event contention deadline.
    // Full compatibility projection is admitted here but runs on its serial worker without automation locks.
    // Companion/before-launch I/O stays outside _stateLock. Dependencies must not re-enter lifecycle methods.
    // Stop crosses this boundary before invalidation,
    // so a validated operation cannot begin after Stop has completed.
    private readonly object _companionLock = new();
    private long _generation;
    private Action<ProcessEventInfo>? _processStartedHandler;
    private Action<ProcessEventInfo>? _processStoppedHandler;

    private readonly SessionTracker _sessionTracker = new();
    private readonly SessionSuspendHandler _sessionSuspend;

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
        ILocationCacheStore? locationCacheStore = null,
        TimeProvider? timeProvider = null,
        TimeSpan? speedGatePollInterval = null,
        TimeSpan? speedGateTimeout = null,
        IGameLauncher? gameLauncher = null,
        ISystemPowerEvents? powerEvents = null)
    {
        _monitor = monitor;
        _gameConfiguration = gameConfiguration;
        _hdr = hdr;
        _gameLauncher = gameLauncher;
        _pathResolver = pathResolver;
        _playTime = playTime;
        _breakReminder = breakReminder;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _logger = logger;
        _launchOwnership = new LaunchOwnershipCoordinator(gameLauncher, hdr, _timeProvider, logger,
            _stateLock, () => _started, () => _configIndex, _sessionTracker, PersistInFlightSnapshot);
        _monitorMode = new MonitorModeCoordinator(monitorModeController, hdr, locationSource, locationCacheStore,
            _timeProvider, logger, _stateLock, CaptureMonitorModeSnapshot);
        _companionPolicy = new Ds4WindowsAutomationPolicy(gameLauncher, logger);
        _afterExitActions = new AfterExitActionQueue(_stateLock, generation => _started && generation == _generation,
            action => (_gameLauncher ?? throw new InvalidOperationException("动作执行器不可用。")).ExecuteAction(action),
            _timeProvider, logger);
        _processMatcher = new GameProcessMatcher(_configIndex, _pathResolver, _logger);
        _speedAttachment = new SpeedAttachmentCoordinator(
            speedController, _timeProvider, _logger, processIdentityProbe,
            speedGatePollInterval, speedGateTimeout);
        _inFlightRecovery = new InFlightSessionRecovery(monitor, playTime, inFlightSessionStore,
            processIdentityProbe, _timeProvider, logger, _sessionTracker, _launchOwnership,
            _speedAttachment, () => _configIndex);
        _sessionSuspend = new SessionSuspendHandler(
            powerEvents, _stateLock, () => _started, _sessionTracker, _inFlightRecovery.SessionStarts,
            _playTime, _timeProvider, _inFlightRecovery.PersistInFlightSnapshot, _logger);
    }

    public void Start()
    {
        var eventTime = _timeProvider.GetLocalNow().DateTime;
        GameConfig[] recoveredGames;
        long generation;
        lock (_stateLock)
        {
            if (_started) return;
            generation = ++_generation;
            var configuration = LoadAndBuildIndexes();
            _speedAttachment.Start();
            _sessionTracker.Clear();
            _sessionSuspend.Reset();
            _launchOwnership.ResetProvenance();
            using var storageEvent = (_playTime as IIdentifiedPlayTimeService)?.BeginEvent(eventTime);
            _inFlightRecovery.RecoverInFlightSessions();
            var recoveredHdrLease = _launchOwnership.OwnsEnabledHdr;
            _launchOwnership.UpdateHdrState();
            if (recoveredHdrLease) _inFlightRecovery.PersistInFlightSnapshot();
            ConfigureProcessObservation();

            _processStartedHandler = info => OnProcessStarted(info, generation);
            _processStoppedHandler = info => OnProcessStopped(info, generation);
            _monitor.ProcessStarted += _processStartedHandler;
            _monitor.ProcessStopped += _processStoppedHandler;
            _sessionSuspend.Subscribe();
            _started = true;
            _afterExitActions.Start(generation);
            try { _breakReminder?.Start(configuration); }
            catch (Exception ex) { _logger.LogWarning(ex, "Failed to start break reminders"); }

            if (_monitorMode.HasController || _inFlightRecovery.HasStore || _gameLauncher is not null)
            {
                try
                {
                    _maintenanceTimer = _timeProvider.CreateTimer(_ => OnMonitorModeTick(generation), null, TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(10));
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to start the monitor mode timer; preset switching will only follow game events.");
                }
            }

            _logger.LogInformation(
                "GameAutomationService started: {Total} configs ({PathCount} path, {ExactNameCount} exact-name, {NameCount} fuzzy-name)",
                _configIndex.All.Count,
                _configIndex.ByPath.Count,
                _configIndex.ByExactName.Count,
                _configIndex.ByName.Length);
            recoveredGames = _sessionTracker.GetActiveDataKeysSnapshot()
                .Where(_configIndex.ByDataKey.ContainsKey).Select(key => _configIndex.ByDataKey[key]).ToArray();
        }

        // Evaluate the preset rule right after startup so the monitor lands in the
        // expected mode instead of waiting for the first game event.
        if (_monitorMode.HasController)
        {
            _monitorMode.Update(forced: true);
        }
        foreach (var game in recoveredGames) ApplyCompanion(game, generation);
    }

    public void ReloadConfig()
    {
        lock (_stateLock)
        {
            var configuration = LoadAndBuildIndexes();
            ConfigureProcessObservation();
            _launchOwnership.ReloadConfig();
            _breakReminder?.ReloadConfig(configuration);
            _logger.LogInformation(
                "GameAutomationService config reloaded: {Total} configs ({PathCount} path, {ExactNameCount} exact-name, {NameCount} fuzzy-name)",
                _configIndex.All.Count,
                _configIndex.ByPath.Count,
                _configIndex.ByExactName.Count,
                _configIndex.ByName.Length);
        }
        _monitorMode.Update(forced: true);
    }

    public void Stop()
    {
        var eventTime = _timeProvider.GetLocalNow().DateTime;
        AfterExitActionQueue.DrainRequest actionDrain;
        lock (_monitorMode.UpdateLock)
        lock (_companionLock)
        {
            lock (_stateLock)
            {
                using var storageEvent = (_playTime as IIdentifiedPlayTimeService)?.BeginEvent(eventTime);
                _started = false;
                ++_generation;
                actionDrain = _afterExitActions.Stop();
                if (_maintenanceTimer is not null)
                {
                    try { _maintenanceTimer.Dispose(); }
                    catch (Exception ex) { _logger.LogDebug(ex, "Failed to dispose the monitor mode timer"); }
                    _maintenanceTimer = null;
                    _monitorMode.Reset();
                }

                try { _breakReminder?.Stop(); }
                catch (Exception ex) { _logger.LogWarning(ex, "Failed to stop break reminders"); }
                Cleanup(() => _monitor.ProcessStarted -= _processStartedHandler, "unsubscribe process starts");
                Cleanup(() => _monitor.ProcessStopped -= _processStoppedHandler, "unsubscribe process stops");
                _processStartedHandler = null;
                _processStoppedHandler = null;
                Cleanup(_sessionSuspend.Unsubscribe, "unsubscribe power events");
                Cleanup(_speedAttachment.CancelAll, "cancel and drain speed attachments");

                foreach (var dataKey in _sessionTracker.GetActiveDataKeysSnapshot())
                {
                    try
                    {
                        _playTime.StopTracking(dataKey);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Failed to flush session for {DataKey}", dataKey);
                        Cleanup(() => _inFlightRecovery.RetainCompletedSession(dataKey), "retain the failed session flush");
                    }
                }

                Cleanup(_sessionTracker.Clear, "_sessionTracker.Clear");
                Cleanup(_launchOwnership.ClearPending, "_pendingLaunches.Clear");
                Cleanup(_launchOwnership.ClearFailures, "_launchFailures.Clear");
                Cleanup(_launchOwnership.ResetProvenance, "_ownLaunchedDataKeys.Clear");
                try { _launchOwnership.UpdateHdrState(); }
                catch (Exception ex) { _logger.LogWarning(ex, "Failed to restore HDR during shutdown"); }
                Cleanup(_inFlightRecovery.ClearSessionStarts, "_inFlightStarts.Clear");
                Cleanup(_inFlightRecovery.ClearProcessStarts, "_inFlightProcessStarts.Clear");
                Cleanup(_speedAttachment.ClearRecords, "_speedAttachment.ClearRecords");

                _inFlightRecovery.CompleteStop();

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
        // An admitted action may call back into read-only service APIs. Never drain it
        // while holding state, companion or monitor-update locks.
        Cleanup(() => _afterExitActions.Drain(actionDrain), "drain after-exit actions");
        if (_playTime is IIdentifiedPlayTimeService identified)
            Cleanup(identified.FlushPendingProjections, "flush compatibility projections");
    }

    private void Cleanup(Action cleanup, string operation)
    {
        try { cleanup(); }
        catch (Exception ex) { _logger.LogWarning(ex, "Failed to {Operation} during shutdown", operation); }
    }

    private void OnProcessStarted(ProcessEventInfo processInfo, long generation)
    {
        var eventTime = _timeProvider.GetLocalNow().DateTime;
        GameConfig? externalGame = null;
        lock (_stateLock)
        {
            if (!_started || generation != _generation) return;
            using var storageEvent = (_playTime as IIdentifiedPlayTimeService)?.BeginEvent(eventTime);
            var normalizedName = PathNormalizer.NormalizeName(processInfo.ExecutableName);
            var config = _processMatcher.Match(processInfo, normalizedName, out var normalizedPath, out var matchLabel);

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
            var isOwnLaunch = _launchOwnership.ObserveStart(config.DataKey, processInfo.ProcessId);
            var processStartedUtc = _inFlightRecovery.RecordProcessStart(processInfo.ProcessId);

            _logger.LogInformation(
                "Process start: DataKey={DataKey}, Via={Match}, Own={OwnLaunch}, Executable={Executable}, Path={Path}, PID={ProcessId}",
                config.DataKey,
                matchLabel,
                isOwnLaunch,
                normalizedName ?? processInfo.ExecutableName ?? "n/a",
                normalizedPath ?? "n/a",
                processInfo.ProcessId);

            if (firstForDataKey)
            {
                try
                {
                    _inFlightRecovery.RecordSessionStart(config.DataKey, isOwnLaunch);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to start tracking for {DataKey}", config.DataKey);
                }

                _speedAttachment.ApplyConfiguredSpeed(config, processInfo.ProcessId, normalizedPath ?? processInfo.ExecutablePath, processStartedUtc);
                if (!isOwnLaunch) externalGame = config;

                // Persist the in-flight snapshot so a restart (deploy or crash) can
                // resume this session instead of losing it.

            }

            ConfigureProcessObservation();
            try { _launchOwnership.UpdateHdrState(); }
            finally { _inFlightRecovery.PersistInFlightSnapshot(); }

            if (!hadAnyActive && _sessionTracker.ActiveCount > 0)
            {
                _logger.LogDebug("Stop events enabled (first active)");
            }
        }

        // Preset switching performs DDC/CI bus I/O; keep it outside the state lock.
        _monitorMode.Update(forced: true);
        if (externalGame is not null) ApplyCompanion(externalGame, generation);
    }

    private void ApplyCompanion(GameConfig game, long generation)
    {
        lock (_companionLock)
        {
            lock (_stateLock)
            {
                if (!_started || generation != _generation) return;
            }
            _companionPolicy.Apply(game);
        }
    }

    private void OnProcessStopped(ProcessEventInfo processInfo, long generation)
    {
        var eventTime = _timeProvider.GetLocalNow().DateTime;
        SpeedAttachmentCoordinator.SpeedAttachRecord? speedAttachCandidate = null;
        string? stoppedDataKey = null;
        GameAction[] exitActions = [];

        lock (_stateLock)
        {
            if (!_started || generation != _generation) return;
            using var storageEvent = (_playTime as IIdentifiedPlayTimeService)?.BeginEvent(eventTime);
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
            stoppedDataKey = entry.DataKey;
            if (entry.ProcessId is int stoppedPid)
            {
                _inFlightRecovery.ReleaseProcess(stoppedPid);
                speedAttachCandidate = _speedAttachment.CaptureExit(stoppedPid);
            }
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
                    var session = _playTime is IIdentifiedPlayTimeService identified
                        ? identified.StopObservedTracking(entry.DataKey) : _playTime.StopTracking(entry.DataKey);
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
                    _inFlightRecovery.RetainCompletedSession(entry.DataKey);
                }
            }

            if (isLastForDataKey)
            {
                if (_configIndex.All.TryGetValue(entry.DataKey, out var endedGame))
                    exitActions = (endedGame.Actions ?? []).Where(action => action.When == "afterExit").ToArray();
                _inFlightRecovery.ReleaseSession(entry.DataKey);
                _launchOwnership.Release(entry.DataKey);
            }
            try { _launchOwnership.UpdateHdrState(); }
            finally { _inFlightRecovery.PersistInFlightSnapshot(); }

            _afterExitActions.Enqueue(exitActions, generation);
            if (_sessionTracker.ActiveCount == 0 && hadAnyActive)
            {
                _logger.LogDebug("Stop events disabled (none active)");
            }
        }

        // File I/O (WER report lookup) stays outside the state lock; see the correlation record
        // captured under it in OnProcessStopped.
        if (speedAttachCandidate is not null)
        {
            _speedAttachment.ReportSpeedAttachExitCorrelation(
                speedAttachCandidate,
                stoppedDataKey ?? "n/a",
                processInfo.ExecutableName,
                _timeProvider.GetUtcNow().UtcDateTime);
        }

        // Preset switching performs DDC/CI bus I/O; keep it outside the state lock.
        _monitorMode.Update(forced: true);
    }

    private AppConfig LoadAndBuildIndexes()
    {
        var configuration = _gameConfiguration.Read();
        _hdr.Configure(configuration.Hdr);
        _speedAttachment.Configure(configuration.CheatEngine);
        _monitorMode.Configure(configuration.MonitorMode);
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

    /// <inheritdoc />
    public IReadOnlyList<GameProcess> GetRunningProcesses()
    { lock (_stateLock) return _inFlightRecovery.GetRunningProcesses(); }
    public void Launch(string dataKey) => LaunchTracked(dataKey, null);
    public void Launch(string dataKey, Guid requestId) => LaunchTracked(dataKey, requestId);
    public IReadOnlyDictionary<string, (Guid RequestId, int ProcessId)> GetLaunchIdentities()
    { lock (_stateLock) return _launchOwnership.GetLaunchIdentities(); }
    private void LaunchTracked(string dataKey, Guid? requestId)
    {
        long generation;
        lock (_stateLock) generation = _generation;
        lock (_companionLock)
        {
            lock (_stateLock)
                if (!_started || generation != _generation) throw new InvalidOperationException("启动已取消。");
            _launchOwnership.Launch(dataKey,
                game => ExecuteActions((game.Actions ?? []).Where(action => action.When == "beforeLaunch").ToArray(), generation),
                () => _started && generation == _generation, requestId);
        }
    }

    private void ExecuteActions(IReadOnlyList<GameAction> actions, long generation)
    {
        foreach (var action in actions)
        {
            lock (_companionLock)
            {
                lock (_stateLock)
                {
                    if (!_started || generation != _generation)
                    {
                        throw new InvalidOperationException("启动已取消。");
                    }
                }
                try
                {
                    if (_gameLauncher is null) throw new InvalidOperationException("动作执行器不可用。");
                    _gameLauncher.ExecuteAction(action);
                }
                catch (Exception ex)
                {
                    throw new InvalidOperationException($"启动前动作失败：{action.Path}：{ex.Message}", ex);
                }
            }
        }
    }

    /// <inheritdoc />
    public IReadOnlyDictionary<string, string> GetGameStates()
    {
        lock (_stateLock) return _launchOwnership.GetGameStates();
    }

    /// <inheritdoc />
    public IReadOnlyDictionary<string, DateTime?> GetRunningSessionStarts()
    {
        lock (_stateLock)
        {
            return _inFlightRecovery.GetRunningSessionStarts();
        }
    }

    // Caller holds _stateLock; the coordinator enters it only after UpdateLock.
    private MonitorModeCoordinator.Snapshot CaptureMonitorModeSnapshot()
    {
        var active = _sessionTracker.GetActiveDataKeysSnapshot();
        return new(active.Count > 0, active.Where(key => _launchOwnership.IsOwnLaunched(key)
            && _configIndex.ByDataKey.TryGetValue(key, out var config) && config.HdrEnabled).ToArray(),
            _launchOwnership.OwnsEnabledHdr, _started);
    }

    // Deferred callback for launch ownership; invoked only after construction, under _stateLock.
    private void PersistInFlightSnapshot() => _inFlightRecovery.PersistInFlightSnapshot();

    private void OnMonitorModeTick(long generation)
    {
        var eventTime = _timeProvider.GetLocalNow().DateTime;
        lock (_stateLock)
        {
            if (!_started || generation != _generation) return;
            using var storageEvent = (_playTime as IIdentifiedPlayTimeService)?.BeginEvent(eventTime);
            var wasOwned = _launchOwnership.OwnsEnabledHdr;
            var expiredCount = _launchOwnership.ExpirePending();
            if (_gameLauncher is not null)
            {
                try { _launchOwnership.UpdateHdrState(); }
                catch (Exception ex) { _logger.LogWarning(ex, "Failed to reconcile launch HDR state"); }
            }
            _inFlightRecovery.RetryPendingRecoveredSessions();
            if (_sessionTracker.ActiveCount > 0 || _inFlightRecovery.PendingCount > 0 || _launchOwnership.PendingCount > 0
                || _launchOwnership.OwnsEnabledHdr || wasOwned || expiredCount > 0) _inFlightRecovery.PersistInFlightSnapshot();
        }
        _monitorMode.Update(forced: false);
    }
}
