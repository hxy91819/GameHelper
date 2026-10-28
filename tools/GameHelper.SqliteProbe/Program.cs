using System.Text.Json;
using GameHelper.Infrastructure.Storage.Sqlite;
using GameHelper.Core.Models;

// Real WAL multi-process behavior, coordinated by named events rather than timing sleeps.
if (args.Length >= 2 && args[0].StartsWith("ops-", StringComparison.Ordinal))
{
    var opsFactory = new SqliteConnectionFactory(args[1]);
    using var c = opsFactory.OpenConnection();
    object? Scalar(string sql, Microsoft.Data.Sqlite.SqliteTransaction? tx = null)
    {
        using var query = c.CreateCommand(); query.CommandText = sql; query.Transaction = tx;
        return query.ExecuteScalar();
    }
    if (args[0] is "ops-hold-reader" or "ops-hold-writer")
    {
        using var tx = c.BeginTransaction(deferred: args[0] == "ops-hold-reader");
        var before = Scalar("SELECT history_revision FROM storage_meta;", tx);
        using var reached = EventWaitHandle.OpenExisting(args[2]);
        using var release = EventWaitHandle.OpenExisting(args[3]);
        reached.Set();
        if (!release.WaitOne(TimeSpan.FromSeconds(30))) throw new TimeoutException("WAL transaction release barrier.");
        if (!Equals(before, Scalar("SELECT history_revision FROM storage_meta;", tx))) throw new InvalidDataException("Reader snapshot changed.");
    }
    else if (args[0] == "ops-write")
    {
        var elapsed = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            new SqliteSessionStore(opsFactory, new ProbeClock(), () => { }).Complete(new()
            {
                SessionId = Guid.NewGuid(), DataKey = "concurrent", StartedAt = DateTime.UnixEpoch
            }, DateTime.UnixEpoch.AddMinutes(1), "observed");
        }
        catch (Microsoft.Data.Sqlite.SqliteException ex) when (ex.SqliteErrorCode is 5 or 6) { Environment.ExitCode = 20; }
        Console.WriteLine(JsonSerializer.Serialize(new { ElapsedMilliseconds = elapsed.ElapsedMilliseconds }));
    }
    else if (args[0] == "ops-checkpoint")
    {
        using var query = c.CreateCommand();
        query.CommandText = args.Length > 2 && args[2] == "truncate" ? "PRAGMA wal_checkpoint(TRUNCATE);" : "PRAGMA wal_checkpoint(PASSIVE);";
        using var row = query.ExecuteReader(); row.Read();
        Console.WriteLine(JsonSerializer.Serialize(new { Busy = row.GetInt64(0), Frames = row.GetInt64(1), Checkpointed = row.GetInt64(2) }));
    }
    return;
}

// Real Desktop two-process optimistic-save driver; no production CLI surface.
if (args.Length >= 2 && args[0].StartsWith("desktop-", StringComparison.Ordinal))
{
    var desktopFactory = new SqliteConnectionFactory(args[1]);
    var config = Path.Combine(args[1], "config.yml");
    if (args[0] == "desktop-prepare")
    {
        var migration = new GameHelper.Infrastructure.Storage.Sqlite.Legacy.SqliteLegacyMigration(desktopFactory, new ProbeClock());
        var candidate = await migration.PrepareAsync([config], sessionsOnly: true);
        await migration.ActivateAsync(candidate.ManifestPath);
        await migration.ImportScopeAsync(config);
        return;
    }
    var desktop = new SqliteDesktopStore(desktopFactory, config);
    var baseline = desktop.Read("journal");
    if (args[0] == "desktop-save")
    {
        using var start = EventWaitHandle.OpenExisting(args[3]);
        using var ready = Semaphore.OpenExisting(args[4]);
        ready.Release();
        if (!start.WaitOne(TimeSpan.FromSeconds(30))) throw new TimeoutException("Desktop save barrier.");
        var document = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(baseline.Json)!;
        document[args[2]] = JsonSerializer.SerializeToElement(new { Status = "在玩", CompletedOn = (string?)null, Note = args[2] });
        try { desktop.Save("journal", baseline, JsonSerializer.Serialize(document)); }
        catch (InvalidOperationException) { Environment.ExitCode = 20; }
    }
    else Console.WriteLine(baseline.Json);
    return;
}

// Session crash driver runs against the production managed/native SQLite assemblies.
if (args.Length >= 2 && args[0].StartsWith("session-", StringComparison.Ordinal))
{
    var sessionFactory = new SqliteConnectionFactory(args[1]);
    await new SqliteDatabaseInitializer(sessionFactory).InitializeAsync();
    var clock = new ProbeClock();
    var original = new InFlightSession
    {
        SessionId = Guid.Parse("987d62d5-d1c7-4397-88cb-544d398fe8b5"), DataKey = "crash-probe",
        StartedAt = clock.GetLocalNow().DateTime.AddMinutes(-20), ProcessId = 1234, NormalizedName = "probe.exe"
    };
    var store = new SqliteSessionStore(sessionFactory, clock, () => { }, point =>
    {
        if (args[0] != "session-crash" || point != args[2]) return;
        using var reached = EventWaitHandle.OpenExisting(args[3]);
        reached.Set();
        // Parent kills this process while the real transaction is either uncommitted or committed.
        using var neverReleased = new ManualResetEvent(false);
        neverReleased.WaitOne();
    });
    store.Load();
    if (args[0] == "session-prepare") store.Save(new() { SnapshotAt = clock.GetLocalNow().DateTime, Sessions = [original] });
    if (args[0] is "session-crash" or "session-recover") store.Complete(original, clock.GetLocalNow().DateTime, "recovered");
    using var check = sessionFactory.OpenConnection();
    using var query = check.CreateCommand();
    query.CommandText = "SELECT (SELECT count(*) FROM play_sessions),(SELECT count(*) FROM inflight_sessions),history_revision FROM storage_meta;";
    using var rows = query.ExecuteReader();
    if (!rows.Read()) throw new InvalidDataException("Missing metadata.");
    Console.WriteLine(JsonSerializer.Serialize(new { Sessions = rows.GetInt64(0), Inflight = rows.GetInt64(1), Revision = rows.GetInt64(2) }));
    return;
}

// Test-only executable; never shipped or registered in production DI.
if (args.Length is not (1 or 3)) throw new ArgumentException("Expected directory [start-event ready-semaphore].");
if (args.Length == 3)
{
    using var start = EventWaitHandle.OpenExisting(args[1]);
    using var ready = Semaphore.OpenExisting(args[2]);
    ready.Release();
    if (!start.WaitOne(TimeSpan.FromSeconds(30))) throw new TimeoutException("Probe start barrier timed out.");
}
var factory = new SqliteConnectionFactory(args[0]);
await new SqliteDatabaseInitializer(factory).InitializeAsync();
using var connection = factory.OpenConnection();
using var command = connection.CreateCommand();
command.CommandText = "SELECT database_id, (SELECT sqlite_version()) FROM storage_meta;";
using var reader = command.ExecuteReader();
if (!reader.Read()) throw new InvalidDataException("Missing initialized storage identity.");
Console.WriteLine(JsonSerializer.Serialize(new { DatabaseId = reader.GetString(0), SqliteVersion = reader.GetString(1) }));

internal sealed class ProbeClock : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => DateTimeOffset.UnixEpoch.AddYears(55);
    public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
}
