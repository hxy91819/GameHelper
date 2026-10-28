using GameHelper.Core.Abstractions;
using GameHelper.Core.Models;
using GameHelper.Infrastructure.Storage.Sqlite;

namespace GameHelper.Infrastructure.Providers;

/// <summary>Tracks identities in memory; only SqliteSessionStore owns durable completion and snapshot transactions.</summary>
public sealed class SqlitePlayTimeService : IIdentifiedPlayTimeService
{
    private readonly object _gate = new();
    private readonly Dictionary<string, InFlightSession> _active = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, InFlightSession> _failed = new(StringComparer.OrdinalIgnoreCase);
    private readonly SqliteSessionStore _store;
    private readonly TimeProvider _clock;
    private readonly Action? _flush;

    public SqlitePlayTimeService(SqliteSessionStore store, TimeProvider clock, Action? flush = null) { _store = store; _clock = clock; _flush = flush; }

    public IDisposable BeginEvent(DateTime eventTime) => _store.BeginEvent(eventTime);
    public DateTime? EventTime => _store.EventTime;
    public void FlushPendingProjections() => _flush?.Invoke();

    public void StartTracking(string gameName) => ResumeTracking(gameName, _clock.GetLocalNow().DateTime, Guid.NewGuid(), null);
    public void ResumeTracking(string gameName, DateTime startTime) => ResumeTracking(gameName, startTime, Guid.NewGuid(), null);
    public void ResumeTracking(string dataKey, DateTime start, Guid sessionId, bool? launchedByHelper)
    {
        lock (_gate)
            _active.TryAdd(dataKey, new InFlightSession { DataKey = dataKey, StartedAt = start, SessionId = sessionId, LaunchedByHelper = launchedByHelper });
    }

    public PlaySession? StopTracking(string gameName)
    {
        InFlightSession session;
        lock (_gate)
        {
            if (!_active.Remove(gameName, out session!)) return null;
            session.RecoveryEndAt = EventTime ?? _clock.GetLocalNow().DateTime;
            _failed[gameName] = session;
        }
        var result = _store.Complete(session, session.RecoveryEndAt.Value, "observed");
        lock (_gate) _failed.Remove(gameName);
        return result;
    }

    /// <summary>The failed attempt's end remains fixed even if the clock advances while I/O fails.</summary>
    public InFlightSession? GetPendingCompletion(string dataKey)
    {
        lock (_gate) return _failed.GetValueOrDefault(dataKey);
    }

    public void WriteRecoveredSession(InFlightSession session, DateTime end)
    {
        _store.Complete(session, end, "recovered");
        lock (_gate)
            if (_failed.TryGetValue(session.DataKey, out var failed) && failed.SessionId == session.SessionId) _failed.Remove(session.DataKey);
    }
    public void WriteRecoveredSession(string gameName, DateTime startTime, DateTime endTime) =>
        throw new InvalidOperationException("SQLite recovery requires the original session ID.");

    public void ShiftActiveStarts(TimeSpan offset)
    {
        lock (_gate) foreach (var session in _active.Values) session.StartedAt += offset;
    }
}
