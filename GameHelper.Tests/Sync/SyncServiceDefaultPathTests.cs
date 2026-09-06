using GameHelper.Core.Abstractions;
using GameHelper.Core.Models;
using GameHelper.Core.Services;
using GameHelper.Core.Utilities;
using Microsoft.Extensions.Logging.Abstractions;

namespace GameHelper.Tests.Sync;

/// <summary>
/// 回归：生产 DI 以无参方式构造 SyncService（不注入 playtimeCsvPath），
/// 构造函数必须回退到 AppDataPath 默认路径，否则 mtime 脏检查对 playtime.csv
/// 恒为 false，自动推送永远不触发。通过 GAMEHELPER_DATA_DIR 沙盒验证默认路径链路。
/// </summary>
[Collection("AppDataPathSequential")]
public sealed class SyncServiceDefaultPathTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _statePath;
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 9, 5, 6, 30, 0, TimeSpan.Zero));
    private readonly FakeConfigProvider _config = new();
    private readonly FakeSnapshotProvider _snapshots = new();
    private readonly FakeChannelProvider _channels = new();

    public SyncServiceDefaultPathTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "GameHelperTests_SyncDefaultPath", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _statePath = Path.Combine(_tempDir, "sync-state.json");
        _config.ConfigPath = Path.Combine(_tempDir, "config.yml");
    }

    [Fact]
    public async Task SyncNowAsync_UsingProductionDefaults_DetectsPlaytimeCsvChanges()
    {
        var sandboxRoot = Path.Combine(_tempDir, "appdata-sandbox");
        var sandboxCsv = Path.Combine(sandboxRoot, "GameHelper", "playtime.csv");
        var originalDataDir = Environment.GetEnvironmentVariable(AppDataPath.DataDirectoryEnvironmentVariable);
        try
        {
            // 与生产 DI 一致：全部可选路径参数走默认解析。
            Environment.SetEnvironmentVariable(AppDataPath.DataDirectoryEnvironmentVariable, sandboxRoot);
            var service = new SyncService(
                _config,
                _snapshots,
                _channels,
                new StatsReportBuilder(),
                _clock,
                NullLogger<SyncService>.Instance);

            Directory.CreateDirectory(Path.GetDirectoryName(sandboxCsv)!);
            File.WriteAllText(sandboxCsv, "game,start_time,end_time,duration_minutes\n");
            File.SetLastWriteTimeUtc(sandboxCsv, _clock.GetUtcNow().UtcDateTime.AddMinutes(-5));
            EnableSync(intervalMinutes: 10);

            var first = await service.SyncNowAsync();
            Assert.Equal(SyncOutcomeStatus.Uploaded, first.Status);

            // 间隔已过、CSV 已更新、内容也变了：脏检查必须放行（DI 缺口时会误判"本地暂无新数据"）。
            _clock.Advance(TimeSpan.FromMinutes(20));
            EnableSync(intervalMinutes: 10, minutes: 90);
            File.SetLastWriteTimeUtc(sandboxCsv, _clock.GetUtcNow().UtcDateTime.AddMinutes(1));

            var second = await service.SyncNowAsync();

            Assert.Equal(SyncOutcomeStatus.Uploaded, second.Status);
            Assert.Equal(2, _channels.Channel.UploadCalls);
        }
        finally
        {
            Environment.SetEnvironmentVariable(AppDataPath.DataDirectoryEnvironmentVariable, originalDataDir);
        }
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

    private void EnableSync(int intervalMinutes, int minutes = 60)
    {
        _config.Config.SyncSettings = new SyncSettings
        {
            Enabled = true,
            Method = "api",
            Repo = "owner/repo",
            Token = "tok",
            IntervalMinutes = intervalMinutes
        };
        _snapshots.Snapshot = new PlaytimeSnapshot(
            new List<GamePlaytimeRecord>
            {
                new()
                {
                    GameName = "game_a",
                    Sessions =
                    {
                        new PlaySession(
                            "game_a",
                            new DateTime(2026, 9, 4, 20, 0, 0),
                            new DateTime(2026, 9, 4, 20, 0, 0).AddMinutes(minutes),
                            TimeSpan.FromMinutes(minutes),
                            minutes)
                    }
                }
            },
            null);
    }

    private sealed class FakeTimeProvider : TimeProvider
    {
        private DateTimeOffset _utcNow;

        public FakeTimeProvider(DateTimeOffset utcNow)
        {
            _utcNow = utcNow;
        }

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

        public Task<StatsUploadResult> UploadAsync(
            SyncSettings settings,
            IReadOnlyList<StatsUploadFile> files,
            string commitMessage,
            CancellationToken cancellationToken = default)
        {
            UploadCalls++;
            return Task.FromResult(new StatsUploadResult("abc1234", NoChanges: false));
        }

        public Task ValidateAsync(SyncSettings settings, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }

    private sealed class FakeChannelProvider : IStatsUploadChannelProvider
    {
        public FakeChannel Channel { get; } = new();

        public IStatsUploadChannel GetChannel(SyncSettings settings) => Channel;
    }
}
