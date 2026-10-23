using System;
using System.Collections.Concurrent;
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
    private const int SpeedAttachRecordTtlMinutes = 60;

    /// <summary>
    /// Upper bound between a Cheat Engine attach and a game exit for the exit to count as a
    /// suspected injection-race crash (the BepInEx interop-generation race crashes within ~1 min).
    /// </summary>
    private static readonly TimeSpan SpeedCrashCorrelationWindow = TimeSpan.FromMinutes(2);

    /// <summary>Minimum gap between repeated in-flight persistence failure logs; a broken data directory must not flood the console.</summary>
    private static readonly TimeSpan InFlightPersistFailureLogInterval = TimeSpan.FromMinutes(10);

    private readonly IProcessMonitor _monitor;
    private readonly IGameConfiguration _gameConfiguration;
    private readonly IHdrController _hdr;
    private readonly IGameLauncher? _gameLauncher;
    private readonly Dictionary<string, DateTimeOffset> _pendingLaunches = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _launchFailures = new(StringComparer.OrdinalIgnoreCase);
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
    private readonly TimeProvider _timeProvider;
    private readonly SpeedAttachScheduler _speedAttachGate;
    /// <summary>Successful Cheat Engine attaches keyed by PID, for game-exit crash correlation. Bounded by pruning.</summary>
    private readonly ConcurrentDictionary<int, SpeedAttachRecord> _recentSpeedAttacks = new();
    private readonly object _monitorModeUpdateLock = new();
    private MonitorModePlan? _monitorModePlan;
    private MonitorModeSettings? _monitorModeSettings;
    private UserCoordinate? _monitorModeCoordinates;
    private DateTime? _monitorModeCoordinatesAt;
    private bool? _lastMonitorModeControllerAvailable;
    private int _locationBootstrapInFlight;
    private long? _locationBootstrapKickTimestamp;
    private ITimer? _monitorModeTimer;
    private bool? _lastMonitorModeAnyGameActive;
    private bool? _lastMonitorModeDaylight;
    private readonly Dictionary<string, DateTime> _inFlightStarts = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<int, DateTime> _inFlightProcessStarts = new();
    /// <summary>活跃会话中的"库内启动"标记：由本方 <see cref="Launch"/> 发起且已被进程观察确认的 dataKey。</summary>
    private readonly HashSet<string> _ownLaunchedDataKeys = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<InFlightSession> _pendingRecoveredSessions = new();
    private readonly ILogger<GameAutomationService> _logger;
    private readonly object _stateLock = new();

    private readonly SessionTracker _sessionTracker = new();
    private readonly ISystemPowerEvents? _powerEvents;

    private AutomationConfigIndex _configIndex = AutomationConfigIndex.Empty;
    private GameProcessMatcher _processMatcher;
    private bool _started;
    private bool _inFlightPersistFailureActive;
    private DateTime _inFlightPersistFailureLoggedAtUtc = DateTime.MinValue;
    /// <summary>系统开始睡眠/休眠的 UTC 时刻；null 表示当前没有进行中的暂停。</summary>
    private DateTime? _suspendStartedAtUtc;

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
        _speedController = speedController;
        _breakReminder = breakReminder;
        _monitorModeController = monitorModeController;
        _inFlightSessionStore = inFlightSessionStore;
        _processIdentityProbe = processIdentityProbe;
        _locationSource = locationSource;
        _locationCacheStore = locationCacheStore;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _powerEvents = powerEvents;
        _logger = logger;
        _processMatcher = new GameProcessMatcher(_configIndex, _pathResolver, _logger);
        _speedAttachGate = new SpeedAttachScheduler(
            _timeProvider,
            _logger,
            request => ApplyAndRecordSpeed(request.DataKey, request.ProcessId, request.Speed),
            _processIdentityProbe,
            speedGatePollInterval,
            speedGateTimeout);
    }

    public void Start()
    {
        lock (_stateLock)
        {
            if (_started) return;
            var configuration = LoadAndBuildIndexes();
            _sessionTracker.Clear();
            _suspendStartedAtUtc = null;
            _ownLaunchedDataKeys.Clear();
            RecoverInFlightSessions();
            var recoveredHdrLease = _hdrScheduler.OwnsEnabledHdr;
            UpdateHdrState();
            if (recoveredHdrLease) PersistInFlightSnapshot();
            ConfigureProcessObservation();

            _monitor.ProcessStarted += OnProcessStarted;
            _monitor.ProcessStopped += OnProcessStopped;
            if (_powerEvents is not null)
            {
                // 先取消再订阅：即使外部在未 Stop 的情况下重复 Start 也不会重复挂接。
                _powerEvents.Suspending -= OnSystemSuspending;
                _powerEvents.Resumed -= OnSystemResumed;
                _powerEvents.Suspending += OnSystemSuspending;
                _powerEvents.Resumed += OnSystemResumed;
            }
            _started = true;
            try { _breakReminder?.Start(configuration); }
            catch (Exception ex) { _logger.LogWarning(ex, "Failed to start break reminders"); }

            if (_monitorModeController is not null || _inFlightSessionStore is not null || _gameLauncher is not null)
            {
                try
                {
                    _monitorModeTimer = _timeProvider.CreateTimer(OnMonitorModeTick, null, TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(10));
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
            foreach (var key in _launchFailures.Keys.Where(key => !_configIndex.All.ContainsKey(key)).ToList()) _launchFailures.Remove(key);
            _breakReminder?.ReloadConfig(configuration);
            _logger.LogInformation(
                "GameAutomationService config reloaded: {Total} configs ({PathCount} path, {ExactNameCount} exact-name, {NameCount} fuzzy-name)",
                _configIndex.All.Count,
                _configIndex.ByPath.Count,
                _configIndex.ByExactName.Count,
                _configIndex.ByName.Length);
        }
        UpdateMonitorModeState(forced: true);
    }

    public void Stop()
    {
        lock (_monitorModeUpdateLock)
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
                    _lastMonitorModeControllerAvailable = null;
                }

                try { _breakReminder?.Stop(); }
                catch (Exception ex) { _logger.LogWarning(ex, "Failed to stop break reminders"); }
                _started = false;
                _monitor.ProcessStarted -= OnProcessStarted;
                _monitor.ProcessStopped -= OnProcessStopped;
                if (_powerEvents is not null)
                {
                    _powerEvents.Suspending -= OnSystemSuspending;
                    _powerEvents.Resumed -= OnSystemResumed;
                }
                _suspendStartedAtUtc = null;
                _speedAttachGate.CancelAll();

                foreach (var dataKey in _sessionTracker.GetActiveDataKeysSnapshot())
                {
                    try
                    {
                        _playTime.StopTracking(dataKey);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Failed to flush session for {DataKey}", dataKey);
                        RetainCompletedSession(dataKey);
                    }
                }

                _sessionTracker.Clear();
                _pendingLaunches.Clear();
                _launchFailures.Clear();
                _ownLaunchedDataKeys.Clear();
                try { UpdateHdrState(); }
                catch (Exception ex) { _logger.LogWarning(ex, "Failed to restore HDR during shutdown"); }
                _inFlightStarts.Clear();
                _inFlightProcessStarts.Clear();
                _recentSpeedAttacks.Clear();

                try
                {
                    if (_pendingRecoveredSessions.Count > 0 || _hdrScheduler.OwnsEnabledHdr) PersistInFlightSnapshot();
                    else _inFlightSessionStore?.Clear();
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
            var consumedPending = _pendingLaunches.Remove(config.DataKey);
            // 监控器启动时枚举到的既有进程（Preexisting）按库内启动对待：GameHelper
            // 无快照重启时已在跑的游戏保持自动化效果，与旧版快照 null 标记的规则一致。
            var isOwnLaunch = consumedPending || processInfo.Preexisting;
            if (isOwnLaunch) _ownLaunchedDataKeys.Add(config.DataKey);
            _launchFailures.Remove(config.DataKey);
            DateTime? processStartedUtc = null;
            if (processInfo.ProcessId is int startedPid
                && _processIdentityProbe?.TryGetStartTimeUtc(startedPid, out var processStart) == true)
            {
                _inFlightProcessStarts[startedPid] = processStart;
                processStartedUtc = processStart;
            }

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
                    _playTime.StartTracking(config.DataKey);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to start tracking for {DataKey}", config.DataKey);
                }

                if (isOwnLaunch)
                {
                    // 外部启动的会话不自动挂变速；库内启动保持原有行为。
                    ApplyConfiguredSpeed(config, processInfo.ProcessId, normalizedPath ?? processInfo.ExecutablePath, processStartedUtc);
                }

                // Persist the in-flight snapshot so a restart (deploy or crash) can
                // resume this session instead of losing it.
                _inFlightStarts[config.DataKey] = DateTime.Now;
            }

            ConfigureProcessObservation();
            try { UpdateHdrState(); }
            finally { PersistInFlightSnapshot(); }

            if (!hadAnyActive && _sessionTracker.ActiveCount > 0)
            {
                _logger.LogDebug("Stop events enabled (first active)");
            }
        }

        // Preset switching performs DDC/CI bus I/O; keep it outside the state lock.
        UpdateMonitorModeState(forced: true);
    }

    private void ApplyConfiguredSpeed(GameConfig config, int? processId, string? gamePath, DateTime? processStartedUtc)
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

        // Cheat Engine injects helper DLLs into the game on attach. When a mod loader (BepInEx /
        // MelonLoader) boots alongside, its sensitive startup phase races that injection and
        // crashes the game with zero logs. Without a resolvable game path the loader cannot be
        // detected, so the legacy immediate attach applies (same as speedAttach: immediate).
        var loaderInfo = ModLoaderDetector.Detect(gamePath);
        if (IsImmediateSpeedAttach(config)
            || loaderInfo.Kind == ModLoaderKind.None
            || loaderInfo.LogPath is null
            || gamePath is null)
        {
            if (loaderInfo.Kind != ModLoaderKind.None)
            {
                _logger.LogDebug(
                    "speedAttach: immediate configured or unusable loader signal for {DataKey}; attaching speed immediately.",
                    config.DataKey);
            }

            ApplyAndRecordSpeed(config.DataKey, processId.Value, config.SpeedMultiplier.Value);
            return;
        }

        _speedAttachGate.ScheduleDeferred(new SpeedAttachRequest(
            config.DataKey,
            processId.Value,
            config.SpeedMultiplier.Value,
            loaderInfo.LogPath,
            processStartedUtc,
            gamePath));
    }

    /// <summary>
    /// 接管会话时补应用变速。游戏进程在重启前就一直跑着，mod loader 早已完成引导，
    /// 因此仍走 <see cref="ApplyConfiguredSpeed"/>（会命中已存在的 Chainloader 标记或按策略立即附加）。
    /// </summary>
    private void ApplyRecoveredSpeed(InFlightSession session)
    {
        try
        {
            var config = _gameConfiguration.Read().Games.FirstOrDefault(game =>
                string.Equals(game.DataKey, session.DataKey, StringComparison.OrdinalIgnoreCase));
            if (config is null || !config.SpeedMultiplier.HasValue)
            {
                return;
            }

            _logger.LogInformation(
                "Re-applying configured speed {Speed} for the resumed session {DataKey} (PID {ProcessId}).",
                config.SpeedMultiplier.Value,
                session.DataKey,
                session.ProcessId);

            ApplyConfiguredSpeed(config, session.ProcessId, session.NormalizedPath, session.ProcessStartedAtUtc);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to re-apply speed for the recovered session {DataKey}.", session.DataKey);
        }
    }

    private static bool IsImmediateSpeedAttach(GameConfig config) =>
        string.Equals(config.SpeedAttach?.Trim(), GameConfig.SpeedAttachPolicyImmediate, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Performs the actual attach and records it for exit-correlation when the controller really
    /// launched Cheat Engine. Runs both on the immediate path and from the deferred gate callback.
    /// </summary>
    private void ApplyAndRecordSpeed(string dataKey, int processId, double speed)
    {
        try
        {
            if (_speedController!.ApplySpeed(processId, speed))
            {
                var nowUtc = _timeProvider.GetUtcNow().UtcDateTime;
                _recentSpeedAttacks[processId] = new SpeedAttachRecord(dataKey, nowUtc);
                PruneSpeedAttachRecords(nowUtc);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to apply configured speed for {DataKey}", dataKey);
        }
    }

    private void PruneSpeedAttachRecords(DateTime nowUtc)
    {
        foreach (var pair in _recentSpeedAttacks)
        {
            if (nowUtc - pair.Value.AttachedUtc > TimeSpan.FromMinutes(SpeedAttachRecordTtlMinutes))
            {
                _recentSpeedAttacks.TryRemove(pair);
            }
        }
    }

    private sealed record SpeedAttachRecord(string DataKey, DateTime AttachedUtc);

    private void OnProcessStopped(ProcessEventInfo processInfo)
    {
        SpeedAttachRecord? speedAttachCandidate = null;
        string? stoppedDataKey = null;

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
            stoppedDataKey = entry.DataKey;
            if (entry.ProcessId is int stoppedPid)
            {
                _inFlightProcessStarts.Remove(stoppedPid);
                _speedAttachGate.CancelPending(stoppedPid);
                _recentSpeedAttacks.TryRemove(stoppedPid, out speedAttachCandidate);
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
                    RetainCompletedSession(entry.DataKey);
                }
            }

            if (isLastForDataKey)
            {
                _inFlightStarts.Remove(entry.DataKey);
                _ownLaunchedDataKeys.Remove(entry.DataKey);
            }
            try { UpdateHdrState(); }
            finally { PersistInFlightSnapshot(); }

            if (_sessionTracker.ActiveCount == 0 && hadAnyActive)
            {
                _logger.LogDebug("Stop events disabled (none active)");
            }
        }

        // File I/O (WER report lookup) stays outside the state lock; see the correlation record
        // captured under it in OnProcessStopped.
        if (speedAttachCandidate is not null)
        {
            ReportSpeedAttachExitCorrelation(
                speedAttachCandidate,
                stoppedDataKey ?? "n/a",
                processInfo.ExecutableName,
                _timeProvider.GetUtcNow().UtcDateTime);
        }

        // Preset switching performs DDC/CI bus I/O; keep it outside the state lock.
        UpdateMonitorModeState(forced: true);
    }

    /// <summary>
    /// Crash correlation for a game that exited shortly after the speed controller attached Cheat
    /// Engine into it. The BepInEx injection race leaves no game-side logs, so this keeps the
    /// association (PID, attach time, WER report) in GameHelper's own log for diagnosis.
    /// </summary>
    private void ReportSpeedAttachExitCorrelation(
        SpeedAttachRecord record,
        string dataKey,
        string? executableName,
        DateTime stoppedUtc)
    {
        var elapsed = stoppedUtc - record.AttachedUtc;
        if (elapsed > SpeedCrashCorrelationWindow || elapsed < TimeSpan.Zero)
        {
            return;
        }

        var report = WerCrashReportFinder.FindLatest(
            Environment.GetEnvironmentVariable("ProgramData"),
            executableName,
            record.AttachedUtc);
        if (report is not null)
        {
            _logger.LogWarning(
                "Game {DataKey} exited {Elapsed} after the speed controller attached Cheat Engine (attached {Attached}) and Windows archived a crash report: {Report}. " +
                "If this exit was an unexpected startup crash, suspect the BepInEx/Cheat Engine attach race: launch the game once with its speed setting removed so BepInEx rebuilds its interop cache, then re-enable speed.",
                dataKey,
                elapsed,
                record.AttachedUtc,
                report);
        }
        else
        {
            _logger.LogInformation(
                "Game {DataKey} exited {Elapsed} after the speed controller attached Cheat Engine (attached {Attached}); no WER crash report found for {Executable}, so this exit does not look like an injection crash.",
                dataKey,
                elapsed,
                record.AttachedUtc,
                executableName ?? "n/a");
        }
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
        _monitorModeSettings = configuration.MonitorMode;
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
        // 自动化只认库内启动的会话：外部启动的 HDR 偏好不参与调度；
        // pending 启动窗保留（点按钮到进程被观察之间需要提前开 HDR）。
        var keys = _sessionTracker.GetActiveDataKeysSnapshot()
            .Where(IsOwnLaunched)
            .Concat(_pendingLaunches.Keys);
        _hdrScheduler.Update(keys, _configIndex.ByDataKey, _hdr, _logger);
    }

    private bool IsOwnLaunched(string dataKey) => _ownLaunchedDataKeys.Contains(dataKey);

    /// <inheritdoc />
    public void Launch(string dataKey)
    {
        GameLaunchOptions? options = null;
        bool tookOver;
        lock (_stateLock)
        {
            if (!_started || _gameLauncher is null) throw new InvalidOperationException("请先启动游戏监控。");
            if (!_configIndex.All.TryGetValue(dataKey, out var game)) throw new InvalidOperationException("游戏已移除。");
            if (_pendingLaunches.ContainsKey(dataKey))
                throw new InvalidOperationException("该游戏正在启动，请稍候。");

            if (_sessionTracker.GetActiveDataKeysSnapshot().Contains(dataKey, StringComparer.OrdinalIgnoreCase))
            {
                // 游戏（可能由外部启动）已在跑：本方启动标记内的会话拒绝重复启动；
                // 外部启动的会话改为当场接管——不重复拉起，只补齐自动化效果。
                if (_ownLaunchedDataKeys.Contains(dataKey))
                    throw new InvalidOperationException("该游戏已经运行。");
                TakeOverRunningGame(dataKey, game);
                tookOver = true;
            }
            else
            {
                tookOver = false;
                options = game.Launch ?? new GameLaunchOptions { Target = game.ExecutablePath ?? string.Empty };
                _gameLauncher.Validate(options);
                if (!game.IsEnabled)
                {
                    _gameLauncher.Start(options);
                    return;
                }
                _pendingLaunches[dataKey] = _timeProvider.GetUtcNow().AddMinutes(2);
                _launchFailures.Remove(dataKey);
                try
                {
                    if (game.HdrEnabled) _hdrScheduler.PrepareForLaunch(_hdr);
                    PersistInFlightSnapshot();
                }
                catch
                {
                    _pendingLaunches.Remove(dataKey);
                    UpdateHdrState();
                    PersistInFlightSnapshot();
                    throw;
                }
            }
        }

        if (tookOver)
        {
            // 预设切换走 DDC/CI 总线 I/O；与 OnProcessStarted 一致，保持在状态锁外执行。
            UpdateMonitorModeState(forced: true);
            return;
        }
        try
        {
            lock (_stateLock)
            {
                if (!_started || !_pendingLaunches.ContainsKey(dataKey) || !_configIndex.ByDataKey.ContainsKey(dataKey))
                    throw new InvalidOperationException("启动已取消，或该游戏已经运行。");
                _gameLauncher.Start(options!);   // 接管分支已在上方 return；走到这里 options 必已赋值
            }
        }
        catch
        {
            lock (_stateLock)
            {
                _pendingLaunches.Remove(dataKey);
                UpdateHdrState();
                PersistInFlightSnapshot();
            }
            throw;
        }
    }

    /// <summary>
    /// 游戏已在外部运行时点击"启动"：不重复拉起，改为当场接管——把活跃会话标记为库内启动，
    /// 并按现存 PID 补齐自动化效果（HDR 预备、变速补挂）。调用方持有状态锁；
    /// 显示器预设刷新（DDC/CI I/O）由调用方在锁外触发。
    /// </summary>
    private void TakeOverRunningGame(string dataKey, GameConfig game)
    {
        var entry = _sessionTracker.GetActiveEntriesSnapshot().FirstOrDefault(candidate =>
            string.Equals(candidate.DataKey, dataKey, StringComparison.OrdinalIgnoreCase));
        _ownLaunchedDataKeys.Add(dataKey);
        _launchFailures.Remove(dataKey);
        _logger.LogInformation(
            "接管外部启动会话: {DataKey} (PID {ProcessId})；后续按库内启动应用自动化效果。",
            dataKey,
            entry.ProcessId);
        try
        {
            // 游戏已经在跑，HDR 预备失败不应让接管失败；记录后交由下次事件重试。
            if (game.HdrEnabled) _hdrScheduler.PrepareForLaunch(_hdr);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "接管 {DataKey} 时准备 HDR 失败；预设将在下一次事件时重试。", dataKey);
        }

        if (entry.ProcessId is int processId)
        {
            DateTime? processStartedUtc = _inFlightProcessStarts.TryGetValue(processId, out var created) ? created : null;
            ApplyConfiguredSpeed(game, processId, entry.NormalizedPath, processStartedUtc);
        }
        try { UpdateHdrState(); }
        finally { PersistInFlightSnapshot(); }
    }

    /// <inheritdoc />
    public IReadOnlyDictionary<string, string> GetGameStates()
    {
        lock (_stateLock)
        {
            var states = new Dictionary<string, string>(_launchFailures, StringComparer.OrdinalIgnoreCase);
            foreach (var key in _pendingLaunches.Keys) states[key] = "启动中";
            foreach (var key in _sessionTracker.GetActiveDataKeysSnapshot())
            {
                states[key] = _ownLaunchedDataKeys.Contains(key) ? "运行中" : "运行中·外部";
            }
            return states;
        }
    }

    /// <inheritdoc />
    public IReadOnlyDictionary<string, DateTime?> GetRunningSessionStarts()
    {
        lock (_stateLock)
        {
            var starts = new Dictionary<string, DateTime?>(StringComparer.OrdinalIgnoreCase);
            foreach (var key in _sessionTracker.GetActiveDataKeysSnapshot())
            {
                // 用 in-flight 快照的同一来源：跨重启接管时保持原开始时间，休眠调整也同步生效。
                starts[key] = _inFlightStarts.TryGetValue(key, out var startedAt) ? startedAt : null;
            }

            return starts;
        }
    }

    /// <summary>
    /// 系统即将睡眠或休眠：只登记时刻。会话继续持有，唤醒时把暂停跨度从时长里剔除，
    /// 这样睡眠期间既不丢会话，也不把休眠时间计入游玩时长。
    /// </summary>
    private void OnSystemSuspending(object? sender, EventArgs e)
    {
        lock (_stateLock)
        {
            if (!_started)
            {
                _suspendStartedAtUtc = null;
                return;
            }

            _suspendStartedAtUtc = _timeProvider.GetUtcNow().UtcDateTime;
        }

        _logger.LogInformation("System suspending; active game sessions stop counting until resume.");
    }

    /// <summary>
    /// 系统唤醒：把刚过去的时间暂停跨度整体加回各活跃会话的开始时间（等价于剔除休眠时长），
    /// 并把调整后的开始时间写进 in-flight 快照，重启恢复路径保持一致。
    /// </summary>
    private void OnSystemResumed(object? sender, EventArgs e)
    {
        var resumeUtc = _timeProvider.GetUtcNow().UtcDateTime;
        lock (_stateLock)
        {
            if (!_started)
            {
                _suspendStartedAtUtc = null;
                return;
            }

            var suspendedAt = _suspendStartedAtUtc;
            _suspendStartedAtUtc = null;
            if (suspendedAt is not { } suspendUtc || resumeUtc <= suspendUtc) return;

            var paused = resumeUtc - suspendUtc;
            var active = _sessionTracker.GetActiveDataKeysSnapshot();
            if (active.Count == 0) return;

            foreach (var key in active)
            {
                if (_inFlightStarts.TryGetValue(key, out var startedAt))
                {
                    _inFlightStarts[key] = startedAt + paused;
                }
            }

            try
            {
                _playTime.ShiftActiveStarts(paused);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to exclude the paused span from active session starts.");
            }

            // 持久化调整后的开始时间：随后的重启恢复不会把这段暂停记回游玩时长。
            PersistInFlightSnapshot();
            _logger.LogInformation(
                "System resumed after {Paused} of sleep; paused span excluded from {Count} active session(s).",
                paused,
                active.Count);
        }
    }

    private void OnMonitorModeTick(object? state)
    {
        lock (_stateLock)
        {
            if (!_started) return;
            var wasOwned = _hdrScheduler.OwnsEnabledHdr;
            var expired = _pendingLaunches.Where(pair => pair.Value <= _timeProvider.GetUtcNow()).Select(pair => pair.Key).ToList();
            foreach (var key in expired)
            {
                _pendingLaunches.Remove(key);
                _launchFailures[key] = "启动超时：未识别到游戏进程";
            }
            if (_gameLauncher is not null)
            {
                try { UpdateHdrState(); }
                catch (Exception ex) { _logger.LogWarning(ex, "Failed to reconcile launch HDR state"); }
            }
            RetryPendingRecoveredSessions();
            if (_sessionTracker.ActiveCount > 0 || _pendingRecoveredSessions.Count > 0 || _pendingLaunches.Count > 0
                || _hdrScheduler.OwnsEnabledHdr || wasOwned || expired.Count > 0) PersistInFlightSnapshot();
        }
        UpdateMonitorModeState(forced: false);
    }

    /// <summary>
    /// Re-evaluates the monitor preset rule. Gating happens under the state lock, while
    /// the DDC/CI bus I/O inside <see cref="MonitorModeScheduler.Update"/> stays outside
    /// it so a slow monitor never stalls session bookkeeping.
    /// </summary>
    private void UpdateMonitorModeState(bool forced)
    {
        lock (_monitorModeUpdateLock)
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
                if (!_started || plan is null)
                {
                    return;
                }

                anyGameActive = _sessionTracker.GetActiveDataKeysSnapshot().Any(IsOwnLaunched);
                // 显示器从系统消失/回来会改变控制器可用性：这属于必须重新评估的变化
                // （显示器离线期间档位可能停在被强制写入过的状态，回来需要对齐）。
                // location=auto 且还没有坐标时，随 tick 自愈式重试坐标引导：
                // 用户事后在系统设置里打开“定位”无需重启即可拿到日出日落判定。
                if (NeedsAutoLocation(_monitorModeSettings)
                    && (!_monitorModeCoordinatesAt.HasValue || _timeProvider.GetLocalNow().DateTime - _monitorModeCoordinatesAt.Value > LocationCacheMaxAge)
                    && _locationCacheStore is not null
                    && _locationSource is not null)
                {
                    var nowTimestamp = _timeProvider.GetTimestamp();
                    if ((!_locationBootstrapKickTimestamp.HasValue
                         || _timeProvider.GetElapsedTime(_locationBootstrapKickTimestamp.Value, nowTimestamp).TotalMilliseconds >= LocationBootstrapKickCooldownMs)
                        && Interlocked.CompareExchange(ref _locationBootstrapInFlight, 1, 0) == 0)
                    {
                        _locationBootstrapKickTimestamp = nowTimestamp;
                        var settingsSnapshot = _monitorModeSettings!;
                        try
                        {
                            _ = Task.Run(() => BootstrapMonitorModeLocation(settingsSnapshot));
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning(ex, "Failed to kick off the monitor mode location bootstrap.");
                            Interlocked.Exchange(ref _locationBootstrapInFlight, 0);
                        }
                    }
                }

            }

            try
            {
                // Query before even probing DDC availability: HDR presets belong to the OS.
                // Invalidate the gate so returning to SDR re-evaluates unchanged game/daylight state.
                if (!_hdr.TryGetEnabled(out var hdrEnabled) || hdrEnabled)
                {
                    lock (_stateLock)
                    {
                        _lastMonitorModeDaylight = null;
                        _lastMonitorModeControllerAvailable = null;
                    }
                    _logger.LogDebug("HDR is enabled or its state is unknown; skipping monitor preset automation.");
                    return;
                }
                // Availability probing can perform DDC I/O; never hold the session state lock.
                var controllerAvailable = controller.IsAvailable;
                lock (_stateLock)
                {
                    if (!_started) return;
                    var daylight = plan.Daylight.IsDaylight(_timeProvider.GetLocalNow());
                    if (!forced
                        && _lastMonitorModeAnyGameActive == anyGameActive
                        && _lastMonitorModeDaylight == daylight
                        && _lastMonitorModeControllerAvailable == controllerAvailable)
                    {
                        return;
                    }

                    _lastMonitorModeAnyGameActive = anyGameActive;
                    _lastMonitorModeDaylight = daylight;
                    _lastMonitorModeControllerAvailable = controllerAvailable;
                }

                if (!_monitorModeScheduler.Update(plan, anyGameActive, controller, _logger, _timeProvider.GetLocalNow()))
                {
                    lock (_stateLock) _lastMonitorModeDaylight = null;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Monitor mode update failed; the next event or tick will retry.");
                lock (_stateLock)
                {
                    // Invalidate the change gate so subsequent ticks retry even without a state change.
                    _lastMonitorModeDaylight = null;
                    _lastMonitorModeControllerAvailable = null;
                }
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

            if (_inFlightPersistFailureActive)
            {
                _inFlightPersistFailureActive = false;
                _logger.LogInformation("In-flight session snapshot persistence recovered; restart recovery is available again.");
            }
        }
        catch (Exception ex)
        {
            // A data directory the running token cannot write (read-only ACL, low integrity
            // process, antivirus quarantine) fails on every session event. Log the first failure
            // with its stack, then stay quiet until the interval elapses or persistence recovers.
            var nowUtc = _timeProvider.GetUtcNow().UtcDateTime;
            if (!_inFlightPersistFailureActive
                || nowUtc - _inFlightPersistFailureLoggedAtUtc >= InFlightPersistFailureLogInterval)
            {
                _inFlightPersistFailureActive = true;
                _inFlightPersistFailureLoggedAtUtc = nowUtc;
                _logger.LogWarning(
                    ex,
                    "Failed to persist the in-flight session snapshot; restart recovery may be incomplete. Repeated failures are logged at most every {Interval}.",
                    InFlightPersistFailureLogInterval);
            }
        }
    }

    private InFlightSnapshot BuildInFlightSnapshot()
    {
        var snapshot = new InFlightSnapshot
        {
            SnapshotAt = DateTime.Now, Sessions = new List<InFlightSession>(),
            HdrEnabledByGameHelper = _hdrScheduler.OwnsEnabledHdr,
            PendingLaunches = new Dictionary<string, DateTimeOffset>(_pendingLaunches, StringComparer.OrdinalIgnoreCase)
        };
        foreach (var entry in _sessionTracker.GetActiveEntriesSnapshot())
        {
            snapshot.Sessions.Add(new InFlightSession
            {
                DataKey = entry.DataKey,
                ProcessId = entry.ProcessId,
                NormalizedName = entry.NormalizedName,
                NormalizedPath = entry.NormalizedPath,
                ProcessStartedAtUtc = entry.ProcessId is int pid && _inFlightProcessStarts.TryGetValue(pid, out var created) ? created : null,
                StartedAt = _inFlightStarts.TryGetValue(entry.DataKey, out var startedAt) ? startedAt : snapshot.SnapshotAt,
                LaunchedByHelper = _ownLaunchedDataKeys.Contains(entry.DataKey)
            });
        }
        snapshot.Sessions.AddRange(_pendingRecoveredSessions);

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

        if (snapshot is null) return;
        _hdrScheduler.AdoptOwnership(snapshot.HdrEnabledByGameHelper);
        foreach (var pending in snapshot.PendingLaunches ?? new())
        {
            if (pending.Value > _timeProvider.GetUtcNow() && _configIndex.ByDataKey.ContainsKey(pending.Key))
                _pendingLaunches[pending.Key] = pending.Value <= _timeProvider.GetUtcNow().AddMinutes(2) ? pending.Value : _timeProvider.GetUtcNow().AddMinutes(2);
        }
        if (snapshot.Sessions.Count == 0)
        {
            return;
        }

        var resumedCount = 0;
        var closedCount = 0;
        foreach (var group in snapshot.Sessions.GroupBy(session =>
                     (session.DataKey.ToUpperInvariant(), session.StartedAt, session.RecoveryEndAt)))
        {
            var dataKey = group.First().DataKey;
            var liveSessions = new List<InFlightSession>();
            foreach (var session in group)
            {
                if (!session.RecoveryEndAt.HasValue && session.ProcessId.HasValue && IsPersistedProcessStillSame(session, snapshot.SnapshotAt))
                {
                    _sessionTracker.Register(session.DataKey, session.NormalizedName, session.NormalizedPath, session.ProcessId);
                    if (session.ProcessStartedAtUtc is DateTime created) _inFlightProcessStarts[session.ProcessId.Value] = created;
                    // 把接管进程登记为监控层的"已观察"进程：它在本会话里没出现过 START 事件,
                    // 不登记则停止事件与 30 秒漏退出补偿都会无视它,退出即丢时长(曾导致丢失)。
                    _monitor.AssumeRunning(
                        new ProcessEventInfo(
                            session.NormalizedName ?? session.DataKey,
                            session.NormalizedPath,
                            session.ProcessId),
                        snapshot.SnapshotAt.ToUniversalTime());
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
                // 库内启动标记在续算中保持连续：旧版快照（未写标记）按库内对待，
                // 显式标记 false 的外部启动会话在重启后不补自动化效果（时长照常续算）。
                var resumedIsOwn = liveSessions.Any(static session => session.LaunchedByHelper ?? true);
                if (resumedIsOwn) _ownLaunchedDataKeys.Add(dataKey);
                try
                {
                    _playTime.ResumeTracking(dataKey, startedAt);
                    _inFlightStarts[dataKey] = startedAt;
                    resumedCount++;
                    _logger.LogInformation(
                        "Resumed in-flight session for {DataKey}: start {StartTime:t}, {ProcessCount} live process(es), own={OwnLaunch}.",
                        dataKey,
                        startedAt,
                        liveSessions.Count,
                        resumedIsOwn);

                    // 变速挂在被注入的游戏进程上：GameHelper 重启（部署/崩溃）后就没了，
                    // 只在 START 事件里应用会让"重启后仍在跑的这一局"永远没有倍速。
                    if (resumedIsOwn) ApplyRecoveredSpeed(liveSessions[0]);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to resume in-flight session for {DataKey}.", dataKey);
                }
            }
            else
            {
                var startedAt = group.First().StartedAt;
                var endTime = group.First().RecoveryEndAt ?? snapshot.SnapshotAt;
                // When GameHelper stayed down for a long time the true exit time is
                // unknowable; cap the recovered duration so a stale snapshot cannot
                // inflate playtime unboundedly.
                if (endTime - startedAt > MaxRecoveredSessionDuration)
                {
                    _logger.LogWarning(
                        "In-flight session for {DataKey} spans {Span}; capping the recovered duration at {Cap}.",
                        dataKey,
                        endTime - startedAt,
                        MaxRecoveredSessionDuration);
                    endTime = startedAt + MaxRecoveredSessionDuration;
                }

                try
                {
                    _playTime.WriteRecoveredSession(dataKey, startedAt, endTime);
                    closedCount++;
                    _logger.LogInformation(
                        "Closed interrupted session for {DataKey}: start {StartTime:t}, end {EndTime:t}.",
                        dataKey,
                        startedAt,
                        endTime);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to persist interrupted session for {DataKey}.", dataKey);
                    foreach (var pending in group)
                    {
                        pending.RecoveryEndAt = endTime;
                        _pendingRecoveredSessions.Add(pending);
                    }
                }
            }
        }

        foreach (var key in _sessionTracker.GetActiveDataKeysSnapshot()) _pendingLaunches.Remove(key);
        if (resumedCount > 0 || _pendingRecoveredSessions.Count > 0 || _pendingLaunches.Count > 0 || _hdrScheduler.OwnsEnabledHdr)
        {
            // 关键自愈兜底：接管来的会话必须立即重写为新快照。否则下一次
            // 强杀/部署重启时磁盘上没有这份会话，它会被第二次丢失。
            PersistInFlightSnapshot();
            _logger.LogInformation("In-flight snapshot rewritten after recovery with {Count} adopted session(s).", resumedCount);
        }
        else
        {
            try
            {
                // Replace before deleting: a failed delete must not replay completed rows.
                store.Save(BuildInFlightSnapshot());
                store.Clear();
            }
            catch (Exception ex) { _logger.LogWarning(ex, "Failed to clear the completed recovery snapshot; monitoring will continue."); }
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
        lock (_stateLock)
        {
            if (!NeedsAutoLocation(_monitorModeSettings)) return null;
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
                _monitorModeCoordinatesAt = cached.UpdatedAt;
                return coordinate;
            }

            return null;
        }
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
        if (cache is null)
        {
            return;
        }

        try
        {
            var cached = cache.Load();
            var stale = cached is null || (_timeProvider.GetLocalNow().DateTime - cached.UpdatedAt) > LocationCacheMaxAge;

            UserCoordinate? live = null;
            if (stale && _locationSource is not null)
            {
                try
                {
                    live = _locationSource.TryGetLocation();
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
                    "monitorMode location=auto obtained no coordinates (the system 'Location' switch may still be off or no fix yet); daylight falls back to fixed times. Retrying every 60 seconds until coordinates are cached or latitude/longitude is set manually.");
                return;
            }

            lock (_stateLock)
            {
                if (!_started || !ReferenceEquals(settings, _monitorModeSettings) || !NeedsAutoLocation(settings)) return;
                _monitorModeCoordinates = publish;
                _monitorModeCoordinatesAt = live.HasValue ? _timeProvider.GetLocalNow().DateTime : cached!.UpdatedAt;
            }
            UpdateMonitorModeState(forced: true);
        }
        finally
        {
            Interlocked.Exchange(ref _locationBootstrapInFlight, 0);
        }
    }

    private static readonly TimeSpan LocationCacheMaxAge = TimeSpan.FromHours(24);

    /// <summary>定位引导失败后的重试冷却（随监视模式 tick 自愈重试）。</summary>
    private const int LocationBootstrapKickCooldownMs = 60_000;

    private void RetainCompletedSession(string dataKey)
    {
        if (_inFlightStarts.TryGetValue(dataKey, out var start))
            _pendingRecoveredSessions.Add(new InFlightSession
            {
                DataKey = dataKey,
                StartedAt = start,
                RecoveryEndAt = DateTime.Now
            });
    }

    private void RetryPendingRecoveredSessions()
    {
        var completed = new List<InFlightSession>();
        foreach (var group in _pendingRecoveredSessions.GroupBy(s =>
                     (s.DataKey.ToUpperInvariant(), s.StartedAt, s.RecoveryEndAt)))
        {
            var first = group.First();
            try
            {
                _playTime.WriteRecoveredSession(first.DataKey, first.StartedAt, first.RecoveryEndAt!.Value);
                completed.AddRange(group);
            }
            catch (Exception ex) { _logger.LogWarning(ex, "Still unable to persist interrupted session for {DataKey}; retaining snapshot.", first.DataKey); }
        }
        _pendingRecoveredSessions.RemoveAll(completed.Contains);
        if (completed.Count > 0) PersistInFlightSnapshot();
    }

    private bool IsPersistedProcessStillSame(InFlightSession session, DateTime snapshotAt)
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

        if (probe.TryGetStartTimeUtc(session.ProcessId.Value, out var created)
            && (created > snapshotAt.ToUniversalTime()
                || (session.ProcessStartedAtUtc.HasValue && created != session.ProcessStartedAtUtc.Value)))
            return false;

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
