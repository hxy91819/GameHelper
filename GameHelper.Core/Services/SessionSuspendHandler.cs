using System;
using System.Collections.Generic;
using GameHelper.Core.Abstractions;
using Microsoft.Extensions.Logging;

namespace GameHelper.Core.Services;

/// <summary>
/// Owns power-event subscriptions and the current suspend timestamp. Both lifecycle
/// calls and event handlers use the existing automation state lock; no new lock is added.
/// The shared session tracker and in-flight start dictionary are borrowed only under
/// that same lock, including the persistence callback, so resume is atomic with process
/// events, recovery and snapshot reads. This handler never enters the monitor-mode lock.
/// </summary>
internal sealed class SessionSuspendHandler
{
    private readonly ISystemPowerEvents? _powerEvents;
    private readonly object _stateLock;
    private readonly Func<bool> _isStarted;
    private readonly SessionTracker _sessionTracker;
    private readonly Dictionary<string, DateTime> _inFlightStarts;
    private readonly IPlayTimeService _playTime;
    private readonly TimeProvider _timeProvider;
    private readonly Action _persistInFlightSnapshot;
    private readonly ILogger _logger;
    private DateTime? _suspendStartedAtUtc;

    internal SessionSuspendHandler(
        ISystemPowerEvents? powerEvents,
        object stateLock,
        Func<bool> isStarted,
        SessionTracker sessionTracker,
        Dictionary<string, DateTime> inFlightStarts,
        IPlayTimeService playTime,
        TimeProvider timeProvider,
        Action persistInFlightSnapshot,
        ILogger logger)
    {
        _powerEvents = powerEvents;
        _stateLock = stateLock;
        _isStarted = isStarted;
        _sessionTracker = sessionTracker;
        _inFlightStarts = inFlightStarts;
        _playTime = playTime;
        _timeProvider = timeProvider;
        _persistInFlightSnapshot = persistInFlightSnapshot;
        _logger = logger;
    }

    // Lifecycle methods are called by GameAutomationService while holding _stateLock.
    internal void Reset() => _suspendStartedAtUtc = null;

    internal void Subscribe()
    {
        if (_powerEvents is null) return;
        // 先取消再订阅，保持重复启动不会重复挂接的约定。
        _powerEvents.Suspending -= OnSystemSuspending;
        _powerEvents.Resumed -= OnSystemResumed;
        _powerEvents.Suspending += OnSystemSuspending;
        _powerEvents.Resumed += OnSystemResumed;
    }

    internal void Unsubscribe()
    {
        if (_powerEvents is not null)
        {
            try { _powerEvents.Suspending -= OnSystemSuspending; }
            catch (Exception ex) { _logger.LogWarning(ex, "Failed to unsubscribe system suspend events"); }
            try { _powerEvents.Resumed -= OnSystemResumed; }
            catch (Exception ex) { _logger.LogWarning(ex, "Failed to unsubscribe system resume events"); }
        }
        Reset();
    }

    /// <summary>
    /// 系统即将睡眠或休眠：只登记时刻。会话继续持有，唤醒时把暂停跨度从时长里剔除，
    /// 这样睡眠期间既不丢会话，也不把休眠时间计入游玩时长。
    /// </summary>
    private void OnSystemSuspending(object? sender, EventArgs e)
    {
        lock (_stateLock)
        {
            if (!_isStarted())
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
            if (!_isStarted())
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
            _persistInFlightSnapshot();
            _logger.LogInformation(
                "System resumed after {Paused} of sleep; paused span excluded from {Count} active session(s).",
                paused,
                active.Count);
        }
    }
}
