using System.Text.Json;
using GameHelper.Core.Abstractions;
using GameHelper.Core.Models;
using GameHelper.Core.Services;
using GameHelper.Infrastructure.Storage.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace GameHelper.Tests.Sync;

public sealed class SqliteSyncTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "GameHelper.SqliteSync." + Guid.NewGuid().ToString("N"));
    private readonly Clock _clock = new();
    private readonly Config _config = new();
    private readonly Channel _channel = new();
    private SqliteConnectionFactory Factory => new(_directory);
    private SqliteSessionStore Store => new(Factory, _clock, () => { });
    private string StatePath => Path.Combine(_directory, "sync-state.json");
    private SyncService Service => new(_config, Store, _channel, new(), _clock, NullLogger<SyncService>.Instance,
        Path.Combine(_directory, "playtime.csv"), StatePath);
    private async Task Seed()
    {
        Directory.CreateDirectory(_directory);
        await new SqliteDatabaseInitializer(Factory, _clock).InitializeAsync();
        _config.ConfigPath = Path.Combine(_directory, "config.yml");
        Append("first");
    }
    private void Append(string key) => Store.Complete(new()
    {
        SessionId = Guid.NewGuid(), DataKey = key, StartedAt = _clock.GetLocalNow().DateTime.AddMinutes(-20)
    }, _clock.GetLocalNow().DateTime, "observed");
    private void Sql(string sql) { using var c = Factory.OpenConnection(); SqliteConnectionFactory.Execute(c, sql); }
    private SyncState State => JsonSerializer.Deserialize<SyncState>(File.ReadAllText(StatePath), new JsonSerializerOptions(JsonSerializerDefaults.Web))!;

    [Fact]
    public async Task ActualBackupRestore_InvalidatesPersistedSyncBookmark()
    {
        await Seed();
        var service = Service;
        await service.SyncNowAsync();
        var prior = State.HistoryVersion;
        var backup = (await new SqliteDatabaseBackup(Factory, _clock).CreateDailyBackupAsync())!;
        await new SqliteDatabaseBackup(Factory, _clock).RestoreAsync(backup);
        await new SqliteDatabaseInitializer(Factory, _clock).InitializeAsync();
        _clock.Advance();
        Assert.NotEqual(prior, Store.GetHistoryVersion());
        Assert.True(service.GetStatus().HasPendingData);
        await service.SyncNowAsync();
        Assert.Equal(Store.GetHistoryVersion(), State.HistoryVersion);
        Assert.False(service.GetStatus().HasPendingData);
    }

    [Fact]
    public async Task UploadInProgress_NewSessionRemainsPendingAndNextUploadContainsIt()
    {
        await Seed();
        var service = Service;
        var uploadedVersion = Store.GetHistoryVersion();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _channel.Upload = async () => { started.SetResult(); await release.Task; };
        var upload = service.SyncNowAsync();
        await started.Task.WaitAsync(TimeSpan.FromSeconds(15));
        try { Append("during-upload"); }
        finally { release.SetResult(); }
        Assert.Equal(SyncOutcomeStatus.Uploaded, (await upload).Status);
        Assert.Equal(uploadedVersion, State.HistoryVersion);
        Assert.True(service.GetStatus().HasPendingData);
        _clock.Advance(); _channel.Upload = null;
        Assert.Equal(SyncOutcomeStatus.Uploaded, (await service.SyncNowAsync()).Status);
        Assert.Contains("during-upload", _channel.Remote["raw/playtime.csv"]);
        Assert.Equal(Store.GetHistoryVersion(), State.HistoryVersion);
        Assert.False(service.GetStatus().HasPendingData);
    }

    [Fact]
    public async Task HeartbeatCheckpointAndPrivateData_DoNotWakeSync_DatabaseIdentityAndConfigDo()
    {
        await Seed(); var service = Service;
        await service.SyncNowAsync(); _clock.Advance();
        Store.Save(new() { SnapshotAt = _clock.GetLocalNow().DateTime });
        Sql("UPDATE storage_meta SET data_revision=data_revision+1; PRAGMA wal_checkpoint(TRUNCATE);");
        File.WriteAllText(Path.Combine(_directory, "playtime.csv"), "projection mtime is irrelevant");
        File.SetLastWriteTimeUtc(Path.Combine(_directory, "playtime.csv"), _clock.GetUtcNow().UtcDateTime);
        Assert.False(service.GetStatus().HasPendingData);
        Assert.Equal(SyncOutcomeStatus.Skipped, (await service.SyncNowAsync()).Status);
        Assert.Equal(1, _channel.Calls);
        Sql("UPDATE storage_meta SET database_id='restored-database';");
        Assert.True(service.GetStatus().HasPendingData);
        await service.SyncNowAsync(); // Identical bytes still acknowledge the new identity, with no upload.
        Assert.Equal(Store.GetHistoryVersion(), State.HistoryVersion);
        Assert.Equal(1, _channel.Calls);
        Assert.False(service.GetStatus().HasPendingData);
        File.WriteAllText(_config.ConfigPath, "changed config");
        File.SetLastWriteTimeUtc(_config.ConfigPath, _clock.GetUtcNow().UtcDateTime.AddSeconds(1));
        Assert.True(service.GetStatus().HasPendingData);
    }

    [Fact]
    public async Task RawCsvDisabled_WithUnchangedHistory_RemovesPreviouslyManagedRemoteFile()
    {
        await Seed(); var service = Service;
        await service.SyncNowAsync(); Assert.Contains("raw/playtime.csv", _channel.Remote.Keys);
        _clock.Advance(); _config.Value.SyncSettings!.IncludeRawCsv = false;
        File.WriteAllText(_config.ConfigPath, "includeRawCsv: false");
        File.SetLastWriteTimeUtc(_config.ConfigPath, _clock.GetUtcNow().UtcDateTime.AddSeconds(1));
        Assert.Equal(SyncOutcomeStatus.Uploaded, (await service.SyncNowAsync()).Status);
        Assert.DoesNotContain("raw/playtime.csv", _channel.Remote.Keys);
        Assert.Equal(2, _channel.Calls);
    }

    [Fact]
    public async Task OldStateWithoutBookmark_ReevaluatesHistory_ReadFailureNeverAcknowledgesOrUploads()
    {
        await Seed(); var service = Service;
        await service.SyncNowAsync(); var state = State; state.HistoryVersion = null;
        File.WriteAllText(StatePath, JsonSerializer.Serialize(state, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        _clock.Advance(); Assert.True(service.GetStatus().HasPendingData);
        await service.SyncNowAsync(); Assert.NotNull(State.HistoryVersion);
        var bookmark = State.HistoryVersion;
        Sql("DROP TABLE play_sessions;"); // Metadata succeeds but the full snapshot fails.
        Assert.True(Store.GetSnapshot().ReadFailed);
        var failed = await service.SyncNowAsync(force: true);
        Assert.Equal(SyncOutcomeStatus.Failed, failed.Status);
        Assert.Equal(bookmark, State.HistoryVersion); Assert.Equal(1, _channel.Calls);
        Sql("DELETE FROM storage_meta;"); // Cheap revision read failure is also a failure, never a clean skip.
        _clock.Advance();
        Assert.True(service.GetStatus().HasPendingData);
        Assert.Equal(SyncOutcomeStatus.Failed, (await service.SyncNowAsync()).Status);
        Assert.Equal(bookmark, State.HistoryVersion);
    }

    public void Dispose() { if (Directory.Exists(_directory)) Directory.Delete(_directory, true); }
    private sealed class Clock : TimeProvider
    {
        private DateTimeOffset _now = new(2025, 4, 1, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => _now;
        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
        public void Advance() => _now = _now.AddMinutes(10);
    }
    private sealed class Config : IGameConfiguration, IConfigPathProvider
    {
        public string ConfigPath { get; set; } = "";
        public AppConfig Value { get; } = new() { SyncSettings = new() { Enabled = true, Repo = "owner/repo", Method = "api", Token = "test", IntervalMinutes = 5, IncludeRawCsv = true } };
        public AppConfig Read() => Value;
        public AppConfig Change(Action<AppConfig> change) { change(Value); return Value; }
    }
    private sealed class Channel : IStatsUploadChannel, IStatsUploadChannelProvider
    {
        public Func<Task>? Upload { get; set; }
        public int Calls { get; private set; }
        public Dictionary<string, string> Remote { get; } = [];
        public IStatsUploadChannel GetChannel(SyncSettings settings) => this;
        public Task ValidateAsync(SyncSettings settings, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public async Task<StatsUploadResult> UploadAsync(SyncSettings settings, IReadOnlyList<StatsUploadFile> files, string commitMessage, CancellationToken cancellationToken = default)
        {
            Calls++;
            if (Upload is not null) await Upload();
            foreach (var name in StatsReportBuilder.ManagedFileNames) Remote.Remove(name);
            foreach (var file in files) Remote[file.RelativePath] = file.Content;
            return new("uploaded", NoChanges: false);
        }
    }
}
