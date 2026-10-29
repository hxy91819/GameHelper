using GameHelper.Core.Abstractions;
using GameHelper.Core.Models;
using Microsoft.Extensions.Logging;

namespace GameHelper.Core.Services;

/// <summary>
/// Owns pending library launches, launch provenance, failures and the HDR lease.
/// The caller holds the companion/action execution boundary and supplies the captured
/// lifecycle check. Launch borrows the service state lock for preparation and process
/// creation; beforeStart executes between those boundaries, outside the state lock.
/// Lock order is UpdateLock → companion/actions → state; no new lock is introduced.
/// Launcher/HDR I/O retains its existing state-lock scope; external dependencies must
/// not synchronously re-enter lifecycle methods or monitor updates.
/// </summary>
internal sealed class LaunchOwnershipCoordinator
{
    private readonly Dictionary<string, DateTimeOffset> _pendingLaunches = new(StringComparer.OrdinalIgnoreCase);
    private readonly LaunchSessionCorrelation _requestSessions;
    internal IReadOnlyDictionary<string, (Guid RequestId, int ProcessId)> GetLaunchIdentities() => _requestSessions.Snapshot();
    private readonly Dictionary<string, string> _launchFailures = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _ownLaunchedDataKeys = new(StringComparer.OrdinalIgnoreCase);
    private readonly HdrScheduler _hdrScheduler = new();
    private readonly IGameLauncher? _gameLauncher;
    private readonly IHdrController _hdr;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger _logger;
    private readonly object _stateLock;
    private readonly Func<bool> _isStarted;
    private readonly Func<AutomationConfigIndex> _getConfigIndex;
    private readonly SessionTracker _sessionTracker;
    private readonly Action _persistInFlightSnapshot;

    internal LaunchOwnershipCoordinator(IGameLauncher? gameLauncher, IHdrController hdr,
        TimeProvider timeProvider, ILogger logger, object stateLock, Func<bool> isStarted,
        Func<AutomationConfigIndex> getConfigIndex, SessionTracker sessionTracker, Action persistInFlightSnapshot)
    {
        _requestSessions = new(timeProvider);
        _gameLauncher = gameLauncher;
        _hdr = hdr;
        _timeProvider = timeProvider;
        _logger = logger;
        _stateLock = stateLock;
        _isStarted = isStarted;
        _getConfigIndex = getConfigIndex;
        _sessionTracker = sessionTracker;
        _persistInFlightSnapshot = persistInFlightSnapshot;
    }

    internal bool OwnsEnabledHdr => _hdrScheduler.OwnsEnabledHdr;
    internal int PendingCount => _pendingLaunches.Count;
    internal void ResetProvenance() { _ownLaunchedDataKeys.Clear(); _requestSessions.ClearActive(); }
    internal void ClearPending() { _pendingLaunches.Clear(); _requestSessions.ClearPending(); }
    internal void ClearFailures() => _launchFailures.Clear();
    internal void Release(string dataKey) { _ownLaunchedDataKeys.Remove(dataKey); _requestSessions.Release(dataKey); }

    internal bool ObserveStart(string dataKey, int? processId, bool firstForKey, bool preexisting)
    {
        _requestSessions.Observe(dataKey, processId, firstForKey, preexisting);
        var isOwnLaunch = _pendingLaunches.Remove(dataKey);
        // Existing-process enumeration is external; only a pending library launch grants ownership.
        if (isOwnLaunch) _ownLaunchedDataKeys.Add(dataKey);
        _launchFailures.Remove(dataKey);
        return isOwnLaunch;
    }

    internal void ReloadConfig()
    {
        foreach (var key in _launchFailures.Keys.Where(key => !_getConfigIndex().All.ContainsKey(key)).ToList())
            _launchFailures.Remove(key);
    }

    internal int ExpirePending()
    {
        var expired = _pendingLaunches.Where(pair => pair.Value <= _timeProvider.GetUtcNow()).Select(pair => pair.Key).ToList();
        foreach (var key in expired)
        {
            _pendingLaunches.Remove(key);
            _requestSessions.Cancel(key);
            _launchFailures[key] = "启动超时：未识别到游戏进程";
        }
        return expired.Count;
    }

    internal Dictionary<string, DateTimeOffset> CapturePending() => new(_pendingLaunches, StringComparer.OrdinalIgnoreCase);

    internal void AdoptSnapshot(InFlightSnapshot snapshot)
    {
        _hdrScheduler.AdoptOwnership(snapshot.HdrEnabledByGameHelper);
        foreach (var pending in snapshot.PendingLaunches ?? new())
        {
            if (pending.Value > _timeProvider.GetUtcNow() && _getConfigIndex().ByDataKey.ContainsKey(pending.Key))
                _pendingLaunches[pending.Key] = pending.Value <= _timeProvider.GetUtcNow().AddMinutes(2) ? pending.Value : _timeProvider.GetUtcNow().AddMinutes(2);
        }
    }

    internal void AdoptSession(string dataKey, bool isOwnLaunch)
    {
        if (isOwnLaunch) _ownLaunchedDataKeys.Add(dataKey);
    }

    internal void CompleteRecovery()
    {
        foreach (var key in _sessionTracker.GetActiveDataKeysSnapshot()) { _pendingLaunches.Remove(key); _requestSessions.Cancel(key); }
    }

    internal void UpdateHdrState()
    {
        // HDR 只认库内启动的会话：外部启动的 HDR 偏好不参与调度；
        // pending 启动窗保留（点按钮到进程被观察之间需要提前开 HDR）。
        var keys = _sessionTracker.GetActiveDataKeysSnapshot()
            .Where(IsOwnLaunched)
            .Concat(_pendingLaunches.Keys);
        _hdrScheduler.Update(keys, _getConfigIndex().ByDataKey, _hdr, _logger);
    }

    internal bool IsOwnLaunched(string dataKey) => _ownLaunchedDataKeys.Contains(dataKey);

    internal void Launch(string dataKey, Action<GameConfig> beforeStart, Func<bool> isCurrent, Guid? requestId = null)
    {
        GameLaunchOptions options;
        GameConfig launchGame;
        lock (_stateLock)
        {
            if (!_isStarted() || _gameLauncher is null) throw new InvalidOperationException("请先启动游戏监控。");
            if (!_getConfigIndex().All.TryGetValue(dataKey, out var game)) throw new InvalidOperationException("游戏已移除。");
            if (_pendingLaunches.ContainsKey(dataKey))
                throw new InvalidOperationException("该游戏正在启动，请稍候。");
            if (_sessionTracker.GetActiveDataKeysSnapshot().Contains(dataKey, StringComparer.OrdinalIgnoreCase))
                throw new InvalidOperationException("该游戏已经运行；外部启动不支持运行中切换 HDR。");
            launchGame = game;
            options = game.Launch ?? new GameLaunchOptions { Target = game.ExecutablePath ?? string.Empty };
            _gameLauncher.Validate(options);
            if (game.IsEnabled)
            {
                _pendingLaunches[dataKey] = _timeProvider.GetUtcNow().AddMinutes(2);
                _launchFailures.Remove(dataKey);
                try
                {
                    if (game.HdrEnabled) _hdrScheduler.PrepareForLaunch(_hdr, _logger);
                    _persistInFlightSnapshot();
                }
                catch
                {
                    _pendingLaunches.Remove(dataKey);
                    _requestSessions.Cancel(dataKey);
                    UpdateHdrState();
                    _persistInFlightSnapshot();
                    throw;
                }
            }
        }
        try
        {
            beforeStart(launchGame);
            lock (_stateLock)
            {
                if (!isCurrent() || (launchGame.IsEnabled && (!_pendingLaunches.ContainsKey(dataKey) || !_getConfigIndex().ByDataKey.ContainsKey(dataKey))))
                    throw new InvalidOperationException("启动已取消，或该游戏已经运行。");
                if (requestId is { } id)
                {
                    var pid = _gameLauncher.StartTracked(options);
                    if (_pendingLaunches.TryGetValue(dataKey, out var deadline))
                        // The launch gate above rejected any already-running DataKey.
                        _requestSessions.Begin(dataKey, id, pid, deadline, alreadyRunning: false);
                }
                else _gameLauncher.Start(options);
            }
        }
        catch
        {
            lock (_stateLock)
            {
                // Reentrant Stop/Start must not let an old launch clear a new lifecycle's state.
                if (isCurrent())
                {
                    _pendingLaunches.Remove(dataKey);
                    _requestSessions.Cancel(dataKey);
                    UpdateHdrState();
                    _persistInFlightSnapshot();
                }
            }
            throw;
        }
    }

    internal IReadOnlyDictionary<string, string> GetGameStates()
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
