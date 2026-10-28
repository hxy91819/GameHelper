using System.Text.Json;
using GameHelper.Infrastructure.Storage.Sqlite;
using GameHelper.Core.Models;

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
