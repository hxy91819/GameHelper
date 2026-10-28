using System.Text.Json;
using GameHelper.Infrastructure.Storage.Sqlite;

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
