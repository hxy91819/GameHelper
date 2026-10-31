using GameHelper.Core.Models;
using GameHelper.Infrastructure.Storage.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace GameHelper.Tests;

public sealed class SqliteProjectionSelectionTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "gh-projection-selection-" + Guid.NewGuid().ToString("N"));
    private readonly ManualResetEventSlim _snapshotProjected = new();
    private readonly ManualResetEventSlim _csvProjected = new();
    private readonly ManualResetEventSlim _sideProjected = new();
    private int _historyCaptures;
    private volatile bool _failCsv;
    private SessionStorage? _storage;
    private SqliteConnectionFactory Factory => new(_root);
    private string Config => Path.Combine(_root, "config.yml");
    private string Csv => Path.Combine(_root, "playtime.csv");

    private async Task<SessionStorage> Start()
    {
        _storage = new SessionStorage(Factory, TimeProvider.System, NullLoggerFactory.Instance);
        await _storage.InitializeAsync(Config, [], checkpoint: point =>
        {
            if (point == "projection.capture.history") Interlocked.Increment(ref _historyCaptures);
            if (point == "projection.replace.before:playtime.csv" && _failCsv) throw new IOException("locked CSV");
            if (point == "projection.replace.after:playtime.csv") _csvProjected.Set();
            if (point == "projection.replace.after:inflight-sessions.json") _snapshotProjected.Set();
            if (point == "projection.replace.after:config.yml.desktop-journal.json") _sideProjected.Set();
        });
        _storage.InFlight.Load();
        Reset();
        return _storage;
    }

    private void Reset() { _snapshotProjected.Reset(); _csvProjected.Reset(); _sideProjected.Reset(); Interlocked.Exchange(ref _historyCaptures, 0); }
    private void Heartbeat(SessionStorage storage) => storage.InFlight.Save(new() { SnapshotAt = DateTime.UnixEpoch.AddYears(55) });
    private static void Wait(ManualResetEventSlim signal) => Assert.True(signal.Wait(TimeSpan.FromSeconds(10)), "projection reached the requested target");

    [Fact]
    public async Task HeartbeatSkipsHistoryAndExistingCsvButCompletionStillProjectsFullHistory()
    {
        var storage = await Start();
        using (var locked = new FileStream(Csv, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            Heartbeat(storage);
            Wait(_snapshotProjected);
            Assert.Null(storage.Warning);
            Assert.Equal(0, Volatile.Read(ref _historyCaptures));
            Assert.False(_csvProjected.IsSet);
        }
        Reset();
        var end = DateTime.UnixEpoch.AddYears(55);
        ((SqliteSessionStore)storage.InFlight).Complete(new()
            { SessionId = Guid.NewGuid(), DataKey = "completed", StartedAt = end.AddMinutes(-20) }, end, "observed");
        Wait(_csvProjected);
        Assert.Contains("completed", File.ReadAllText(Csv));
        Assert.True(Volatile.Read(ref _historyCaptures) > 0);
    }

    [Fact]
    public async Task MissingTargetsAndEditedSideDocumentsAreRepairedByPendingExport()
    {
        var storage = await Start();
        File.Delete(Csv);
        var side = new SqliteDesktopStore(Factory, Config);
        var snapshot = side.Read("journal");
        side.Save("journal", snapshot, "{\"game\":{\"Status\":\"在玩\",\"Note\":\"changed\"}}");
        Heartbeat(storage);
        Wait(_snapshotProjected);
        Wait(_sideProjected);
        // Full flush is a barrier only after pending export has had its chance.
        Assert.True(File.Exists(Csv));
        Assert.Contains("changed", File.ReadAllText(Config + ".desktop-journal.json"));
    }

    [Fact]
    public async Task ProjectionFailureRetainsDirtyRetryAndExplicitFlushRepairsExternalEdits()
    {
        var storage = await Start();
        File.Delete(Csv);
        _failCsv = true;
        Heartbeat(storage);
        Assert.True(SpinWait.SpinUntil(() => storage.Warning is not null, TimeSpan.FromSeconds(10)));
        _failCsv = false;
        Heartbeat(storage);
        Wait(_snapshotProjected);
        Assert.True(File.Exists(Csv));
        File.WriteAllText(Csv, "external modification");
        storage.FlushProjection();
        Assert.DoesNotContain("external modification", File.ReadAllText(Csv));
        Assert.Null(storage.Warning);
    }

    public void Dispose()
    {
        _storage?.Dispose(); _snapshotProjected.Dispose(); _csvProjected.Dispose(); _sideProjected.Dispose();
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }
}
