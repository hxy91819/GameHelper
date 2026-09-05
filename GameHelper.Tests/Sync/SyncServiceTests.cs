using GameHelper.Core.Abstractions;
using GameHelper.Core.Models;
using GameHelper.Core.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace GameHelper.Tests.Sync;

public class SyncServiceTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _csvPath;
    private readonly string _statePath;
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 9, 5, 6, 30, 0, TimeSpan.Zero));
    private readonly FakeConfigProvider _config = new();
    private readonly FakeSnapshotProvider _snapshots = new();
    private readonly FakeChannelProvider _channels = new();
    private readonly SyncService _service;

    public SyncServiceTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "GameHelperTests_Sync", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _csvPath = Path.Combine(_tempDir, "playtime.csv");
        _statePath = Path.Combine(_tempDir, "sync-state.json");
        _config.ConfigPath = Path.Combine(_tempDir, "config.yml");

        _service = new SyncService(
            _config,
            _snapshots,
            _channels,
            new StatsReportBuilder(),
            _clock,
            NullLogger<SyncService>.Instance,
            _csvPath,
            _statePath);
    }

    [Fact]
    public async Task SyncNowAsync_WhenNotConfigured_Skips()
    {
        var outcome = await _service.SyncNowAsync();

        Assert.Equal(SyncOutcomeStatus.Skipped, outcome.Status);
        Assert.Equal(0, _channels.Channel.UploadCalls);
    }

    [Fact]
    public async Task SyncNowAsync_WhenDisabled_Skips()
    {
        _config.Config.SyncSettings = new SyncSettings { Enabled = false, Repo = "owner/repo" };

        var outcome = await _service.SyncNowAsync();

        Assert.Equal(SyncOutcomeStatus.Skipped, outcome.Status);
        Assert.Equal(0, _channels.Channel.UploadCalls);
    }

    [Fact]
    public async Task SyncNowAsync_WithInvalidRepoFormat_Fails()
    {
        _config.Config.SyncSettings = new SyncSettings { Enabled = true, Repo = "not-a-repo" };
        WriteCsv();

        var outcome = await _service.SyncNowAsync();

        Assert.Equal(SyncOutcomeStatus.Failed, outcome.Status);
        Assert.Contains("owner/name", outcome.Error);
    }

    [Fact]
    public async Task SyncNowAsync_FirstRunWithData_UploadsAndWritesState()
    {
        EnableSync();
        WriteCsv();

        var outcome = await _service.SyncNowAsync();

        Assert.Equal(SyncOutcomeStatus.Uploaded, outcome.Status);
        Assert.Equal("abc1234", outcome.CommitId);
        Assert.Equal(1, _channels.Channel.UploadCalls);
        Assert.True(File.Exists(_statePath));

        var state = ReadState();
        Assert.Equal(_clock.GetUtcNow().UtcDateTime, state.LastSuccessUtc);
        Assert.Null(state.LastError);
        Assert.NotNull(state.ContentHash);
    }

    [Fact]
    public async Task SyncNowAsync_WithinInterval_SkipsEvenWithNewData()
    {
        EnableSync();
        await FirstSyncAsync();

        _clock.Advance(TimeSpan.FromMinutes(10));
        TouchCsv();

        var outcome = await _service.SyncNowAsync();

        Assert.Equal(SyncOutcomeStatus.Skipped, outcome.Status);
        Assert.Equal(1, _channels.Channel.UploadCalls);
    }

    [Fact]
    public async Task SyncNowAsync_AfterInterval_WithoutNewData_Skips()
    {
        EnableSync();
        await FirstSyncAsync();

        _clock.Advance(TimeSpan.FromDays(2));

        var outcome = await _service.SyncNowAsync();

        Assert.Equal(SyncOutcomeStatus.Skipped, outcome.Status);
        Assert.Equal(1, _channels.Channel.UploadCalls);
    }

    [Fact]
    public async Task SyncNowAsync_AfterInterval_WithUnchangedContent_SkipsUploadButRefreshesState()
    {
        EnableSync(intervalMinutes: 10);
        await FirstSyncAsync();
        var firstState = ReadState();

        _clock.Advance(TimeSpan.FromMinutes(20));
        TouchCsv();

        var outcome = await _service.SyncNowAsync();

        Assert.Equal(SyncOutcomeStatus.Skipped, outcome.Status);
        Assert.Equal(1, _channels.Channel.UploadCalls);
        var state = ReadState();
        Assert.Equal(firstState.ContentHash, state.ContentHash);
        Assert.Equal(_clock.GetUtcNow().UtcDateTime, state.LastSuccessUtc);
    }

    [Fact]
    public async Task SyncNowAsync_WhenChannelFails_RecordsErrorAndBacksOff()
    {
        EnableSync(intervalMinutes: 10);
        WriteCsv();
        _channels.Channel.Throw = new InvalidOperationException("boom");

        var failed = await _service.SyncNowAsync(force: true);
        Assert.Equal(SyncOutcomeStatus.Failed, failed.Status);
        Assert.Equal("boom", ReadState().LastError);

        // 失败退避期内：即使到了间隔、有新数据，也不自动重试。
        _clock.Advance(TimeSpan.FromMinutes(10));
        TouchCsv();
        var backedOff = await _service.SyncNowAsync();
        Assert.Equal(SyncOutcomeStatus.Skipped, backedOff.Status);
        Assert.Equal(1, _channels.Channel.UploadCalls);

        // 退避期内 force 可以穿透。
        var forced = await _service.SyncNowAsync(force: true);
        Assert.Equal(SyncOutcomeStatus.Failed, forced.Status);
        Assert.Equal(2, _channels.Channel.UploadCalls);
    }

    [Fact]
    public async Task SyncNowAsync_Force_BypassesInterval()
    {
        EnableSync();
        await FirstSyncAsync();

        _clock.Advance(TimeSpan.FromMinutes(5));
        TouchCsv();
        var outcome = await _service.SyncNowAsync(force: true);

        Assert.Equal(SyncOutcomeStatus.Uploaded, outcome.Status);
        Assert.Equal(2, _channels.Channel.UploadCalls);
    }

    [Fact]
    public async Task SyncNowAsync_WithNoPlayData_Skips()
    {
        EnableSync();
        WriteCsv();
        _snapshots.Snapshot = new PlaytimeSnapshot(new List<GamePlaytimeRecord>(), _csvPath);

        var outcome = await _service.SyncNowAsync();

        Assert.Equal(SyncOutcomeStatus.Skipped, outcome.Status);
        Assert.Equal(0, _channels.Channel.UploadCalls);
    }

    [Fact]
    public async Task ValidateAsync_WithEnabledConfig_CallsChannel()
    {
        EnableSync();

        var outcome = await _service.ValidateAsync();

        Assert.Equal(SyncOutcomeStatus.Validated, outcome.Status);
        Assert.Equal(1, _channels.Channel.ValidateCalls);
    }

    [Fact]
    public void GetStatus_ReflectsPendingDataAndState()
    {
        EnableSync();
        WriteCsv();

        var before = _service.GetStatus();
        Assert.True(before.HasPendingData);
        Assert.Equal(default, before.LastSuccessUtc);

        _service.SyncNowAsync().GetAwaiter().GetResult();

        var after = _service.GetStatus();
        Assert.False(after.HasPendingData);
        Assert.Equal(_clock.GetUtcNow().UtcDateTime, after.LastSuccessUtc);
    }

    [Fact]
    public void GetStatus_DetectsConfigChangeAsPending()
    {
        EnableSync();
        WriteCsv();
        _service.SyncNowAsync().GetAwaiter().GetResult();

        // 换了目标仓库：旧状态作废，应当重新推送。
        _config.Config.SyncSettings = new SyncSettings
        {
            Enabled = true,
            Repo = "owner/other-repo"
        };

        var status = _service.GetStatus();
        Assert.True(status.HasPendingData);
        Assert.Equal(default, status.LastSuccessUtc);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDir))
            {
                Directory.Delete(_tempDir, recursive: true);
            }
        }
        catch
        {
            // best-effort cleanup
        }
    }

    private void EnableSync(int intervalMinutes = SyncSettings.DefaultIntervalMinutes)
    {
        _config.Config.SyncSettings = new SyncSettings
        {
            Enabled = true,
            Method = "api",
            Repo = "owner/repo",
            Token = "tok",
            IntervalMinutes = intervalMinutes
        };
        _snapshots.Snapshot = new PlaytimeSnapshot(SampleRecords(), _csvPath);
    }

    private async Task FirstSyncAsync()
    {
        WriteCsv();
        var outcome = await _service.SyncNowAsync();
        Assert.Equal(SyncOutcomeStatus.Uploaded, outcome.Status);
    }

    private static List<GamePlaytimeRecord> SampleRecords() => new()
    {
        new()
        {
            GameName = "game_a",
            Sessions =
            {
                new PlaySession(
                    "game_a",
                    new DateTime(2026, 9, 4, 20, 0, 0),
                    new DateTime(2026, 9, 4, 21, 0, 0),
                    TimeSpan.FromHours(1),
                    60)
            }
        }
    };

    private void WriteCsv()
    {
        File.WriteAllText(_csvPath, "game,start_time,end_time,duration_minutes\n");
        // 固定 mtime 为时钟之前，避免真实文件系统时间与假时钟竞争导致的不确定。
        File.SetLastWriteTimeUtc(_csvPath, _clock.GetUtcNow().UtcDateTime.AddMinutes(-5));
    }

    private void TouchCsv() => File.SetLastWriteTimeUtc(_csvPath, _clock.GetUtcNow().UtcDateTime.AddMinutes(1));

    private SyncState ReadState()
    {
        Assert.True(File.Exists(_statePath));
        return System.Text.Json.JsonSerializer.Deserialize<SyncState>(
            File.ReadAllText(_statePath),
            new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web))
            ?? throw new InvalidOperationException("state missing");
    }

    private sealed class FakeTimeProvider : TimeProvider
    {
        private DateTimeOffset _utcNow;

        public FakeTimeProvider(DateTimeOffset utcNow)
        {
            _utcNow = utcNow;
        }

        public DateTimeOffset UtcNow => _utcNow;

        public void Advance(TimeSpan time) => _utcNow += time;

        public override DateTimeOffset GetUtcNow() => _utcNow;
    }

    private sealed class FakeConfigProvider : IGameConfiguration, IConfigPathProvider
    {
        public AppConfig Config { get; set; } = new();

        public string ConfigPath { get; set; } = string.Empty;

        public AppConfig Read() => Config;

        public AppConfig Change(Action<AppConfig> change)
        {
            change(Config);
            return Config;
        }
    }

    private sealed class FakeSnapshotProvider : IPlaytimeSnapshotProvider
    {
        public PlaytimeSnapshot Snapshot { get; set; } = new(new List<GamePlaytimeRecord>(), null);

        public IReadOnlyList<GamePlaytimeRecord> GetPlaytimeRecords() => Snapshot.Records;

        public PlaytimeSnapshot GetSnapshot() => Snapshot;
    }

    private sealed class FakeChannel : IStatsUploadChannel
    {
        public int UploadCalls { get; private set; }

        public int ValidateCalls { get; private set; }

        public Exception? Throw { get; set; }

        public Task<StatsUploadResult> UploadAsync(
            SyncSettings settings,
            IReadOnlyList<StatsUploadFile> files,
            string commitMessage,
            CancellationToken cancellationToken = default)
        {
            UploadCalls++;
            if (Throw is not null)
            {
                throw Throw;
            }

            return Task.FromResult(new StatsUploadResult("abc1234", NoChanges: false));
        }

        public Task ValidateAsync(SyncSettings settings, CancellationToken cancellationToken = default)
        {
            ValidateCalls++;
            if (Throw is not null)
            {
                throw Throw;
            }

            return Task.CompletedTask;
        }
    }

    private sealed class FakeChannelProvider : IStatsUploadChannelProvider
    {
        public FakeChannel Channel { get; } = new();

        public IStatsUploadChannel GetChannel(SyncSettings settings) => Channel;
    }
}
