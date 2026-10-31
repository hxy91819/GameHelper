using GameHelper.Core.Abstractions;
using GameHelper.Core.Models;
using GameHelper.Infrastructure.Providers;
using GameHelper.Infrastructure.Storage.Sqlite.Legacy;
using Microsoft.Extensions.Logging;
using static GameHelper.Infrastructure.Storage.Sqlite.Legacy.LegacySql;

namespace GameHelper.Infrastructure.Storage.Sqlite;

/// <summary>Chooses one authoritative backend before the host exposes monitoring or IPC.</summary>
public sealed class SessionStorage : IDisposable
{
    private readonly SqliteConnectionFactory _factory;
    private readonly TimeProvider _clock;
    private readonly ILoggerFactory _logs;
    private readonly ISessionRatingNotifier? _notifier;
    private SqliteLegacyCompatibility? _compatibility;
    private SerialCompatibilityProjection? _projections;
    private IPlayTimeService? _playTime;
    private IInFlightSessionStore? _inflight;
    private IPlaytimeSnapshotProvider? _snapshot;
    private string? _configPath;
    private SqliteSessionStore? _sessionStore;
    private string? _warning;
    private string? _storageFailure;
    private string? _maintenanceFailure;

    public SessionStorage(SqliteConnectionFactory factory, TimeProvider clock, ILoggerFactory logs, ISessionRatingNotifier? notifier = null)
    { _factory = factory; _clock = clock; _logs = logs; _notifier = notifier; }

    public bool UsesSqlite => _compatibility is not null;
    public string? Warning
    {
        get
        {
            var messages = new[] { Volatile.Read(ref _storageFailure), Volatile.Read(ref _maintenanceFailure), Volatile.Read(ref _warning) }
                .Where(message => message is not null).ToArray();
            return messages.Length == 0 ? null : string.Join("\n", messages);
        }
    }
    public IPlayTimeService PlayTime => _playTime ?? throw new InvalidOperationException("Session storage is not initialized.");
    public IInFlightSessionStore InFlight => _inflight ?? throw new InvalidOperationException("Session storage is not initialized.");
    public IPlaytimeSnapshotProvider Snapshot => _snapshot ?? throw new InvalidOperationException("Session storage is not initialized.");

    /// <summary>Only pre-activation failures fall back. Existing main databases fail closed, preserving a single authority.</summary>
    public async Task InitializeAsync(string configPath, IReadOnlyList<GameConfig> games, Action? ensureLegacyWritersStopped = null,
        Action<string>? checkpoint = null, CancellationToken cancellationToken = default)
    {
        if (_playTime is not null) return;
        _configPath = configPath;
        var migration = new SqliteLegacyMigration(_factory, _clock, games, checkpoint);
        if (!File.Exists(_factory.DatabasePath))
        {
            try
            {
                ensureLegacyWritersStopped?.Invoke();
                var candidate = await migration.PrepareAsync([configPath], cancellationToken: cancellationToken, sessionsOnly: true).ConfigureAwait(false);
                await migration.ActivateAsync(candidate.ManifestPath, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException && !File.Exists(_factory.DatabasePath))
            {
                Volatile.Write(ref _warning, "SQLite 迁移未激活，继续使用旧存储：" + ex.Message);
                _logs.CreateLogger<SessionStorage>().LogWarning(ex, "{StorageWarning}", Warning);
                _playTime = new CsvBackedPlayTimeService(_factory.DataDirectory, _logs.CreateLogger<CsvBackedPlayTimeService>(), _clock, migrateLegacyJson: false);
                _inflight = new JsonInFlightSessionStore(Path.Combine(_factory.DataDirectory, "inflight-sessions.json"), _logs.CreateLogger<JsonInFlightSessionStore>());
                _snapshot = new FilePlaytimeSnapshotProvider(_factory.DataDirectory);
                return;
            }
            catch (Exception ex) when (ex is not OperationCanceledException && File.Exists(_factory.DatabasePath))
            {
                _logs.CreateLogger<SessionStorage>().LogWarning(ex, "Activation response failed after database publication; checking durable activation.");
            }
        }
        // A post-rename failure is an activated database: never revert to the source files.
        await new SqliteDatabaseInitializer(_factory, _clock).InitializeAsync(cancellationToken).ConfigureAwait(false);
        using (var c = _factory.OpenConnection())
        {
            var activated = Scalar(c, null, "SELECT value FROM migration_state WHERE name='activated';") as string
                ?? throw new InvalidDataException("Main SQLite database has no activation record; legacy fallback refused.");
            try
            {
                checkpoint?.Invoke("activation.diagnostic.repair.before");
                WriteJson(Path.Combine(_factory.DataDirectory, "migration-complete.json"), new { MigrationId = activated });
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { _logs.CreateLogger<SessionStorage>().LogWarning(ex, "Could not repair the diagnostic migration receipt; database activation remains authoritative."); }
        }
        await migration.ImportScopeAsync(configPath, cancellationToken).ConfigureAwait(false);
        _compatibility = new SqliteLegacyCompatibility(_factory, _clock, games, checkpoint, preserveHistoryKinds: true);
        var projections = new SerialCompatibilityProjection(ExportProjection, ReportProjectionFailure, ExportPendingProjection);
        _projections = projections;
        var store = new SqliteSessionStore(_factory, _clock, projections.Request, checkpoint, ReportStorageFailure, () => Volatile.Write(ref _storageFailure, null));
        _sessionStore = store;
        _inflight = store;
        _snapshot = store;
        _playTime = new SqlitePlayTimeService(store, _clock, FlushProjection, _notifier);
        try { await projections.FlushAsync().ConfigureAwait(false); }
        catch { /* Worker already reported the projection fault; committed SQLite remains authoritative. */ }
    }

    /// <summary>Returns scoped journal and history together; unactivated storage keeps the legacy Desktop merge.</summary>
    public PlaytimeSnapshot GetReportSnapshot() => _sessionStore is not null
        ? _sessionStore.GetReportSnapshot(_configPath!) : Snapshot.GetReportSnapshot();

    /// <summary>Drains legacy projections for handoff/diagnostics. SQLite reads and sync do not depend on projections.</summary>
    public void FlushProjection()
    {
        _projections?.Flush();
    }

    /// <summary>Called only by the background worker, outside automation locks. Projection faults never hide backup faults.</summary>
    public async Task MaintainAsync(CancellationToken cancellationToken = default)
    {
        if (!UsesSqlite) return;
        try
        {
            await _projections!.FlushAsync(full: false).ConfigureAwait(false);
        }
        catch { /* Projection worker reports and retains its own warning. */ }
        try
        {
            var started = System.Diagnostics.Stopwatch.GetTimestamp();
            var maintenance = new SqliteStorageMaintenance(_factory, _clock);
            var backup = await maintenance.RunAsync(cancellationToken).ConfigureAwait(false);
            if (maintenance.Completed) Volatile.Write(ref _maintenanceFailure, null);
            if (backup is not null)
                _logs.CreateLogger<SessionStorage>().LogInformation("SQLite daily backup published: {Backup}; health/backup elapsed {ElapsedMilliseconds} ms",
                    backup, System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            Volatile.Write(ref _maintenanceFailure, File.Exists(_factory.HealthFailurePath)
                ? "SQLite 完整性失败，已停止写入与同步；停止全部实例后显式恢复：" + ex.Message
                : "SQLite 健康维护／每日备份失败；请检查日志，备份将在下次维护重试：" + ex.Message);
            _logs.CreateLogger<SessionStorage>().LogError(ex, "{StorageWarning}", Warning);
        }
    }

    private void ReportStorageFailure(Exception exception)
    {
        Volatile.Write(ref _storageFailure, "SQLite 存储故障；保留在途重试，不切回旧存储：" + exception.Message);
        _logs.CreateLogger<SessionStorage>().LogError(exception, "{StorageWarning}", Warning);
    }

    private void ExportProjection()
    {
        _compatibility!.ExportAsync().GetAwaiter().GetResult();
        Volatile.Write(ref _warning, null);
    }

    private void ExportPendingProjection()
    {
        _compatibility!.ExportPendingAsync().GetAwaiter().GetResult();
        Volatile.Write(ref _warning, null);
    }

    private void ReportProjectionFailure(Exception exception)
    {
        Volatile.Write(ref _warning, "SQLite 已保存；旧版兼容投影失败，暂不能降级：" + exception.Message);
        _logs.CreateLogger<SessionStorage>().LogError(exception, "{StorageWarning}", Warning);
    }

    public void Dispose() => _projections?.Dispose();
}
