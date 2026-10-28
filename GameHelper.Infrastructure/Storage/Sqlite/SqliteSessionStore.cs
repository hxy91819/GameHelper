using GameHelper.Core.Abstractions;
using GameHelper.Core.Models;
using GameHelper.Infrastructure.Storage.Sqlite.Legacy;
using Microsoft.Data.Sqlite;
using static GameHelper.Infrastructure.Storage.Sqlite.Legacy.LegacySql;

namespace GameHelper.Infrastructure.Storage.Sqlite;

/// <summary>
/// ConsoleHost's session writer. Every call owns a short connection and transaction; no callbacks,
/// process probes or automation locks are acquired in a transaction. The caller serializes its generation.
/// </summary>
public sealed class SqliteSessionStore : IInFlightSessionStore, IPlaytimeSnapshotProvider
{
    private readonly SqliteConnectionFactory _factory;
    private readonly TimeProvider _clock;
    private readonly Action<string>? _checkpoint;
    private readonly Action _project;
    private readonly AsyncLocal<SqliteEventWriteBudget?> _event = new();
    private long _revision;
    private readonly Action<Exception>? _reportFailure;
    private readonly Action? _reportRecovery;

    /// <summary>The projection callback only enqueues work after commit; it must not execute projection synchronously.</summary>
    public SqliteSessionStore(SqliteConnectionFactory factory, TimeProvider clock, Action project, Action<string>? checkpoint = null, Action<Exception>? reportFailure = null, Action? reportRecovery = null)
    { _factory = factory; _clock = clock; _project = project; _checkpoint = checkpoint; _reportFailure = reportFailure; _reportRecovery = reportRecovery; }

    /// <summary>Owns one event's write budget. Nested events restore the previous scope.</summary>
    public IDisposable BeginEvent(DateTime eventTime)
    {
        var previous = _event.Value;
        _event.Value = new SqliteEventWriteBudget(eventTime);
        return new EventScope(() => _event.Value = previous);
    }

    public DateTime? EventTime => _event.Value?.EventTime;

    private SqliteTransaction BeginWrite(SqliteConnection connection)
    {
        _checkpoint?.Invoke("write.acquire.before");
        return (_event.Value ?? new SqliteEventWriteBudget(_clock.GetLocalNow().DateTime)).BeginWrite(connection);
    }

    private sealed class EventScope(Action restore) : IDisposable
    {
        public void Dispose() => restore();
    }

    public InFlightSnapshot? Load() => Guard(LoadCore);

    private InFlightSnapshot? LoadCore()
    {
        using var c = _factory.OpenConnection();
        using var tx = c.BeginTransaction(deferred: true);
        var snapshot = LegacyDatabaseReader.Snapshot(c, tx);
        _revision = snapshot?.Revision ?? 0;
        return snapshot;
    }

    public void Save(InFlightSnapshot snapshot) => Guard(() => { SaveCore(snapshot); return true; });

    private void SaveCore(InFlightSnapshot snapshot)
    {
        using (var c = _factory.OpenConnection())
        using (var tx = BeginWrite(c))
        {
            CheckRevision(c, tx);
            // Never resurrect a completed ID from an old in-memory snapshot after a lost commit response.
            var durable = new InFlightSnapshot
            {
                SnapshotAt = snapshot.SnapshotAt, HdrEnabledByGameHelper = snapshot.HdrEnabledByGameHelper,
                PendingLaunches = snapshot.PendingLaunches,
                Sessions = snapshot.Sessions.Where(s => Scalar(c, tx, "SELECT 1 FROM play_sessions WHERE session_id=$0;", s.SessionId.ToString("D")) is null).ToList()
            };
            if (durable.Sessions.Any(s => s.SessionId == Guid.Empty)) throw new InvalidDataException("SQLite runtime snapshot requires stable session IDs.");
            LegacyRelationalWriter.Snapshot(c, tx, "runtime", durable);
            Execute(c, tx, "UPDATE runtime_snapshot SET revision=$0;", _revision + 1);
            Dirty(c, tx, history: false);
            _checkpoint?.Invoke("snapshot.commit.before");
            tx.Commit();
            snapshot.Revision = ++_revision;
            _checkpoint?.Invoke("snapshot.commit.after");
        }
        _project();
    }

    public void Clear() => Save(new InFlightSnapshot { SnapshotAt = _clock.GetLocalNow().DateTime });

    /// <summary>History, revisions, in-flight deletion and projection dirtiness commit together, exactly once per ID.</summary>
    public PlaySession Complete(InFlightSession session, DateTime end, string completionSource)
    {
        var result = Guard(() => CompleteCore(session, end, completionSource));
        _reportRecovery?.Invoke();
        return result;
    }

    private PlaySession CompleteCore(InFlightSession session, DateTime end, string completionSource)
    {
        var duration = end > session.StartedAt ? end - session.StartedAt : TimeSpan.Zero;
        var result = new PlaySession(session.DataKey, session.StartedAt, end, duration, (long)duration.TotalMinutes);
        using (var c = _factory.OpenConnection())
        using (var tx = BeginWrite(c))
        {
            CheckRevision(c, tx);
            var id = session.SessionId.ToString("D");
            if (session.SessionId == Guid.Empty) throw new InvalidDataException("Completion requires a stable session ID.");
            if (Scalar(c, tx, "SELECT 1 FROM play_sessions WHERE session_id=$0;", id) is null)
            {
                var sequence = Convert.ToInt64(Scalar(c, tx, "SELECT coalesce(max(sequence),0)+1 FROM play_sessions;"));
                Execute(c, tx, "INSERT OR IGNORE INTO history_keys VALUES($0,$1);", session.DataKey, sequence);
                Execute(c, tx, """
                    INSERT INTO play_sessions VALUES($0,$1,$2,$3,$4,$5,$6,$7,$8,$9,$10,$11,NULL,NULL);
                    """, id, session.DataKey, sequence, Date(session.StartedAt), Date(end), (int)session.StartedAt.Kind,
                    (int)end.Kind, duration.Ticks, result.DurationMinutes,
                    session.LaunchedByHelper is null ? "unknown" : session.LaunchedByHelper.Value ? "library" : "external",
                    completionSource, _clock.GetUtcNow().ToString("O"));
                Execute(c, tx, "UPDATE storage_meta SET history_revision=history_revision+1,data_revision=data_revision+1;");
            }
            Execute(c, tx, "DELETE FROM inflight_processes WHERE session_id=$0; DELETE FROM inflight_sessions WHERE session_id=$0;", id);
            var nextRevision = _revision + Execute(c, tx, "UPDATE runtime_snapshot SET revision=revision+1;");
            Dirty(c, tx, history: true);
            _checkpoint?.Invoke("completion.commit.before");
            tx.Commit();
            // Set our expected revision before the after-commit fault point (lost acknowledgements are retryable).
            _revision = nextRevision;
            _checkpoint?.Invoke("completion.commit.after");
        }
        _project();
        return result;
    }

    public IReadOnlyList<GamePlaytimeRecord> GetPlaytimeRecords() => GetSnapshot().Records;

    public PlaytimeSnapshot GetSnapshot()
    {
        try { return Guard(GetSnapshotCore); }
        catch { return new([], null, ReadFailed: true); }
    }

    private PlaytimeSnapshot GetSnapshotCore()
    {
        using var c = _factory.OpenConnection();
        using var tx = c.BeginTransaction(deferred: true);
        var version = ReadHistoryVersion(c, tx);
        _checkpoint?.Invoke("history.read.version");
        return new(LegacyDatabaseReader.History(c, tx), null, HistoryVersion: version);
    }

    /// <summary>Reads only metadata without acquiring automation state or projection coordination locks.</summary>
    public PlaytimeHistoryVersion GetHistoryVersion() => Guard(() =>
    {
        using var c = _factory.OpenConnection();
        using var tx = c.BeginTransaction(deferred: true);
        return ReadHistoryVersion(c, tx);
    });

    private static PlaytimeHistoryVersion ReadHistoryVersion(SqliteConnection c, SqliteTransaction tx)
    {
        using var command = Command(c, tx, "SELECT database_id,history_revision FROM storage_meta;");
        using var reader = command.ExecuteReader();
        if (!reader.Read()) throw new InvalidDataException("SQLite history metadata is missing.");
        return new(reader.GetString(0), reader.GetInt64(1));
    }

    private T Guard<T>(Func<T> operation)
    {
        try { return operation(); }
        catch (Exception ex) { _reportFailure?.Invoke(ex); throw; }
    }

    private void CheckRevision(SqliteConnection c, SqliteTransaction tx)
    {
        var actual = Convert.ToInt64(Scalar(c, tx, "SELECT coalesce((SELECT revision FROM runtime_snapshot),0);"));
        if (actual != _revision) throw new InvalidOperationException("In-flight snapshot changed since recovery; stale writer refused.");
    }

    private void Dirty(SqliteConnection c, SqliteTransaction tx, bool history)
    {
        foreach (var file in history ? new[] { "playtime.csv", "inflight-sessions.json" } : new[] { "inflight-sessions.json" })
            Execute(c, tx, """
                INSERT INTO compatibility_exports VALUES($0,(SELECT data_revision FROM storage_meta),
                    coalesce((SELECT revision FROM runtime_snapshot),0),'','',1)
                ON CONFLICT(target) DO UPDATE SET dirty=1;
                """, Path.Combine(_factory.DataDirectory, file));
    }
}
