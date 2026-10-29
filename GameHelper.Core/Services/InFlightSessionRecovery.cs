using GameHelper.Core.Abstractions;
using GameHelper.Core.Models;
using GameHelper.Core.Utilities;
using Microsoft.Extensions.Logging;

namespace GameHelper.Core.Services;

/// <summary>
/// Owns in-flight starts, process creation identities, snapshot persistence and startup
/// adoption, including failed-history retries and recovered speed application.
/// Every member borrows the service state lock; no lock or asynchronous entry is added.
/// Callers keep generation validation before event/timer mutations. Lock order remains
/// UpdateLock → companion → state; callbacks never enter the first two locks.
/// Only short session/snapshot transactions retain the state-lock scope. Full compatibility
/// projection is queued to an independent serial worker; see sqlite-session-storage.md.
/// </summary>
internal sealed class InFlightSessionRecovery
{
    private static readonly TimeSpan InFlightPersistFailureLogInterval = TimeSpan.FromMinutes(10);
    private readonly IProcessMonitor _monitor;
    private readonly IPlayTimeService _playTime;
    private readonly IInFlightSessionStore? _inFlightSessionStore;
    private readonly IProcessIdentityProbe? _processIdentityProbe;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger _logger;
    private readonly SessionTracker _sessionTracker;
    private readonly LaunchOwnershipCoordinator _launchOwnership;
    private readonly SpeedAttachmentCoordinator _speedAttachment;
    private readonly Func<AutomationConfigIndex> _getConfigIndex;
    private readonly Dictionary<string, DateTime> _inFlightStarts = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<int, DateTime> _inFlightProcessStarts = new();
    private readonly List<InFlightSession> _pendingRecoveredSessions = new();
    private readonly Dictionary<string, Guid> _sessionIds = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, bool?> _sessionSources = new(StringComparer.OrdinalIgnoreCase);
    private long _snapshotRevision;
    private bool _inFlightPersistFailureActive;
    private DateTime _inFlightPersistFailureLoggedAtUtc = DateTime.MinValue;

    internal InFlightSessionRecovery(IProcessMonitor monitor, IPlayTimeService playTime,
        IInFlightSessionStore? store, IProcessIdentityProbe? processIdentityProbe,
        TimeProvider timeProvider, ILogger logger, SessionTracker sessionTracker,
        LaunchOwnershipCoordinator launchOwnership, SpeedAttachmentCoordinator speedAttachment,
        Func<AutomationConfigIndex> getConfigIndex)
    {
        _monitor = monitor;
        _playTime = playTime;
        _inFlightSessionStore = store;
        _processIdentityProbe = processIdentityProbe;
        _timeProvider = timeProvider;
        _logger = logger;
        _sessionTracker = sessionTracker;
        _launchOwnership = launchOwnership;
        _speedAttachment = speedAttachment;
        _getConfigIndex = getConfigIndex;
    }

    private DateTime LocalEventTime() => (_playTime as IIdentifiedPlayTimeService)?.EventTime ?? _timeProvider.GetLocalNow().DateTime;

    internal bool HasStore => _inFlightSessionStore is not null;
    internal int PendingCount => _pendingRecoveredSessions.Count;

    // Borrowed only by SessionSuspendHandler under the same service state lock, so
    // paused-span adjustments and persisted start times continue to share one source.
    internal Dictionary<string, DateTime> SessionStarts => _inFlightStarts;

    internal DateTime? RecordProcessStart(int? processId)
    {
        if (processId is int startedPid
            && _processIdentityProbe?.TryGetStartTimeUtc(startedPid, out var processStart) == true)
        {
            _inFlightProcessStarts[startedPid] = processStart;
            return processStart;
        }
        return null;
    }

    internal void RecordSessionStart(string dataKey, bool ownLaunch)
    {
        var start = LocalEventTime();
        var id = Guid.NewGuid();
        _inFlightStarts[dataKey] = start;
        _sessionIds[dataKey] = id;
        _sessionSources[dataKey] = ownLaunch;
        if (_playTime is IIdentifiedPlayTimeService identified) identified.ResumeTracking(dataKey, start, id, ownLaunch);
        else _playTime.StartTracking(dataKey);
    }
    internal void ReleaseSession(string dataKey) { _inFlightStarts.Remove(dataKey); _sessionIds.Remove(dataKey); _sessionSources.Remove(dataKey); }
    internal void ReleaseProcess(int processId) => _inFlightProcessStarts.Remove(processId);
    internal void ClearSessionStarts() { _inFlightStarts.Clear(); _sessionIds.Clear(); _sessionSources.Clear(); }
    internal void ClearProcessStarts() => _inFlightProcessStarts.Clear();

    internal IReadOnlyList<GameProcess> GetRunningProcesses() => _sessionTracker.GetActiveEntriesSnapshot()
        .Where(e => e.ProcessId is not null).Select(e => new GameProcess(e.DataKey, _sessionIds.GetValueOrDefault(e.DataKey),
            e.ProcessId!.Value, _inFlightProcessStarts.TryGetValue(e.ProcessId.Value, out var start) ? start : null)).ToArray();

    internal IReadOnlyDictionary<string, DateTime?> GetRunningSessionStarts()
    {
        var starts = new Dictionary<string, DateTime?>(StringComparer.OrdinalIgnoreCase);
        foreach (var key in _sessionTracker.GetActiveDataKeysSnapshot())
        {
            // Recovery and suspend adjustments retain the original session start source.
            starts[key] = _inFlightStarts.TryGetValue(key, out var startedAt) ? startedAt : null;
        }
        return starts;
    }

    internal void CompleteStop()
    {
        try
        {
            if (_pendingRecoveredSessions.Count > 0 || _launchOwnership.OwnsEnabledHdr) PersistInFlightSnapshot();
            else _inFlightSessionStore?.Clear();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to clear the in-flight session snapshot after stopping");
        }
    }

    /// <summary>
    /// 接管会话时补应用变速。游戏进程在重启前就一直跑着，mod loader 早已完成引导，
    /// 因此仍走 <see cref="SpeedAttachmentCoordinator.ApplyConfiguredSpeed"/>（会命中已存在的 Chainloader 标记或按策略立即附加）。
    /// </summary>
    private void ApplyRecoveredSpeed(InFlightSession session)
    {
        try
        {
            if (!_getConfigIndex().ByDataKey.TryGetValue(session.DataKey, out var config) || !config.SpeedMultiplier.HasValue)
            {
                return;
            }

            _logger.LogInformation(
                "Re-applying configured speed {Speed} for the resumed session {DataKey} (PID {ProcessId}).",
                config.SpeedMultiplier.Value,
                session.DataKey,
                session.ProcessId);

            _speedAttachment.ApplyConfiguredSpeed(config, session.ProcessId, session.NormalizedPath, session.ProcessStartedAtUtc);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to re-apply speed for the recovered session {DataKey}.", session.DataKey);
        }
    }

    /// <summary>
    /// Persists the current in-flight session snapshot. Callers must hold or be inside
    /// the state lock so the tracker snapshot is consistent.
    /// </summary>
    internal void PersistInFlightSnapshot()
    {
        var store = _inFlightSessionStore;
        if (store is null)
        {
            return;
        }

        try
        {
            var snapshot = BuildInFlightSnapshot();
            store.Save(snapshot);
            _snapshotRevision = snapshot.Revision;

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
            Revision = _snapshotRevision,
            SnapshotAt = LocalEventTime(), Sessions = new List<InFlightSession>(),
            HdrEnabledByGameHelper = _launchOwnership.OwnsEnabledHdr,
            PendingLaunches = _launchOwnership.CapturePending()
        };
        foreach (var entry in _sessionTracker.GetActiveEntriesSnapshot())
        {
            snapshot.Sessions.Add(new InFlightSession
            {
                SessionId = _sessionIds.GetValueOrDefault(entry.DataKey),
                DataKey = entry.DataKey,
                ProcessId = entry.ProcessId,
                NormalizedName = entry.NormalizedName,
                NormalizedPath = entry.NormalizedPath,
                ProcessStartedAtUtc = entry.ProcessId is int pid && _inFlightProcessStarts.TryGetValue(pid, out var created) ? created : null,
                StartedAt = _inFlightStarts.TryGetValue(entry.DataKey, out var startedAt) ? startedAt : snapshot.SnapshotAt,
                LaunchedByHelper = _playTime is IIdentifiedPlayTimeService
                    ? _sessionSources.GetValueOrDefault(entry.DataKey) : _launchOwnership.IsOwnLaunched(entry.DataKey)
            });
        }
        snapshot.Sessions.AddRange(_pendingRecoveredSessions);

        return snapshot;
    }

    /// <summary>
    /// Adopts in-flight sessions persisted before a restart: entries whose process is
    /// still the same executable resume timing with the original start time; sessions
    /// whose game exited during the downtime are written as interrupted rows so no
    /// playtime is lost. Runs once during <c>GameAutomationService.Start</c>, under the state lock.
    /// </summary>
    internal void RecoverInFlightSessions()
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
        _snapshotRevision = snapshot.Revision;
        _launchOwnership.AdoptSnapshot(snapshot);
        if (snapshot.Sessions.Count == 0)
        {
            return;
        }

        var resumedCount = 0;
        var closedCount = 0;
        foreach (var group in snapshot.Sessions.GroupBy(session =>
                     (session.SessionId, session.DataKey.ToUpperInvariant(), session.StartedAt, session.RecoveryEndAt)))
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
                // 外部启动仅不参与 HDR，变速与配套软件照常恢复。
                var resumedIsOwn = liveSessions.Any(static session => session.LaunchedByHelper ?? true);
                _launchOwnership.AdoptSession(dataKey, resumedIsOwn);
                try
                {
                    var id = liveSessions[0].SessionId;
                    if (id == Guid.Empty) id = Guid.NewGuid();
                    _sessionIds[dataKey] = id;
                    _sessionSources[dataKey] = liveSessions[0].LaunchedByHelper;
                    if (_playTime is IIdentifiedPlayTimeService identified)
                        identified.ResumeTracking(dataKey, startedAt, id, liveSessions[0].LaunchedByHelper);
                    else _playTime.ResumeTracking(dataKey, startedAt);
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
                    ApplyRecoveredSpeed(liveSessions[0]);
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
                if (!group.First().RecoveryEndAt.HasValue && endTime - startedAt > MaxRecoveredSessionDuration)
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
                    WriteRecovered(group.First(), endTime);
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

        _launchOwnership.CompleteRecovery();
        if (resumedCount > 0 || _pendingRecoveredSessions.Count > 0 || _launchOwnership.PendingCount > 0 || _launchOwnership.OwnsEnabledHdr)
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
                var cleared = BuildInFlightSnapshot();
                store.Save(cleared);
                _snapshotRevision = cleared.Revision;
                store.Clear();
            }
            catch (Exception ex) { _logger.LogWarning(ex, "Failed to clear the completed recovery snapshot; monitoring will continue."); }
        }

        _logger.LogInformation(
            "In-flight session recovery finished: {Resumed} session(s) resumed, {Closed} session(s) closed.",
            resumedCount,
            closedCount);
    }

    internal void RetainCompletedSession(string dataKey)
    {
        if (_playTime is IIdentifiedPlayTimeService identified && identified.GetPendingCompletion(dataKey) is { } failed)
        {
            _pendingRecoveredSessions.Add(failed);
            return;
        }
        if (_inFlightStarts.TryGetValue(dataKey, out var start))
            _pendingRecoveredSessions.Add(new InFlightSession
            {
                SessionId = _sessionIds.GetValueOrDefault(dataKey),
                DataKey = dataKey,
                StartedAt = start,
                RecoveryEndAt = LocalEventTime()
            });
    }

    internal void RetryPendingRecoveredSessions()
    {
        var completed = new List<InFlightSession>();
        foreach (var group in _pendingRecoveredSessions.GroupBy(s =>
                     (s.SessionId, s.DataKey.ToUpperInvariant(), s.StartedAt, s.RecoveryEndAt)))
        {
            var first = group.First();
            try
            {
                WriteRecovered(first, first.RecoveryEndAt!.Value);
                completed.AddRange(group);
            }
            catch (Exception ex) { _logger.LogWarning(ex, "Still unable to persist interrupted session for {DataKey}; retaining snapshot.", first.DataKey); }
        }
        _pendingRecoveredSessions.RemoveAll(completed.Contains);
        if (completed.Count > 0) PersistInFlightSnapshot();
    }

    private void WriteRecovered(InFlightSession session, DateTime end)
    {
        if (_playTime is IIdentifiedPlayTimeService identified) identified.WriteRecoveredSession(session, end);
        else _playTime.WriteRecoveredSession(session.DataKey, session.StartedAt, end);
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
