using System.Collections.Concurrent;
using GameHelper.Core.Abstractions;
using GameHelper.Core.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace GameHelper.Tests;

public sealed class SpeedAttachSchedulerTests : IDisposable
{
    private readonly string _tempDirectory = Path.Combine(
        Path.GetTempPath(),
        "GameHelperTests",
        Guid.NewGuid().ToString("N"));

    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(25);

    [Fact]
    public async Task Shutdown_WaitsForCallbackThatAlreadyEnteredExecution()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stopStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        var finished = false;
        var scheduler = new SpeedAttachScheduler(TimeProvider.System, NullLogger.Instance, _ =>
        {
            entered.TrySetResult();
            Assert.True(release.Wait(TimeSpan.FromSeconds(10)));
            finished = true;
        }, pollInterval: PollInterval, timeout: TimeSpan.Zero);
        scheduler.ScheduleDeferred(new SpeedAttachRequest("game", 153, 2, null, null, null));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var stop = Task.Run(() =>
        {
            stopStarted.TrySetResult();
            scheduler.CancelAllAndWait();
            Assert.True(finished);
        });
        try
        {
            await stopStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await Task.WhenAny(stop, Task.Delay(100));
            Assert.False(stop.IsCompleted);
        }
        finally
        {
            release.Set();
            await stop.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    [Fact]
    public async Task MarkerWrittenAfterGateStart_AttachesWithoutWaitingForTimeout()
    {
        var logPath = Path.Combine(_tempDirectory, "BepInEx", "LogOutput.log");
        Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);
        File.WriteAllText(logPath, "booting\n");

        var applied = new List<SpeedAttachRequest>();
        var scheduler = new SpeedAttachScheduler(
            TimeProvider.System,
            NullLogger.Instance,
            applied.Add,
            pollInterval: PollInterval,
            timeout: TimeSpan.FromSeconds(30));

        scheduler.ScheduleDeferred(new SpeedAttachRequest("game", 100, 1.5, logPath, null, @"D:\Games\Game\Game.exe"));

        // The chainloader marker lands while the gate is already waiting (like a real boot).
        await Task.Delay(100);
        File.AppendAllText(logPath, "[Message:   BepInEx] Chainloader startup complete\n");

        await WaitForConditionAsync(() => applied.Count > 0, TimeSpan.FromSeconds(5));
        scheduler.CancelAll();
        var request = Assert.Single(applied);
        Assert.Equal(100, request.ProcessId);
        Assert.Equal(1.5, request.Speed);
    }

    [Fact]
    public async Task StaleMarkerFromPreviousRun_DoesNotSatisfyGate_TimeoutFallbackAttaches()
    {
        var logPath = Path.Combine(_tempDirectory, "BepInEx", "LogOutput.log");
        Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);
        File.WriteAllText(logPath, "[Message:   BepInEx] Chainloader startup complete\n");
        // The log is older than the gate start; it must not count as this run's completion.
        File.SetLastWriteTimeUtc(logPath, DateTime.UtcNow.AddHours(-2));

        var applied = new List<SpeedAttachRequest>();
        var scheduler = new SpeedAttachScheduler(
            TimeProvider.System,
            NullLogger.Instance,
            applied.Add,
            pollInterval: PollInterval,
            timeout: TimeSpan.FromMilliseconds(300));

        scheduler.ScheduleDeferred(new SpeedAttachRequest("game", 100, 1.5, logPath, null, null));

        await Task.Delay(1200);
        scheduler.CancelAll();
        Assert.Single(applied);
    }

    [Fact]
    public async Task MarkerWrittenBeforeGateStart_ButAfterProcessStart_AttachesWithoutTimeout()
    {
        var logPath = Path.Combine(_tempDirectory, "BepInEx", "LogOutput.log");
        Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);
        File.WriteAllText(logPath, "[Message:   BepInEx] Chainloader startup complete\n");

        var applied = new List<SpeedAttachRequest>();
        var scheduler = new SpeedAttachScheduler(
            TimeProvider.System,
            NullLogger.Instance,
            applied.Add,
            pollInterval: PollInterval,
            timeout: TimeSpan.FromSeconds(30));

        // The loader booted before the gate existed (fast boot / slow start-event delivery): the
        // marker is newer than the process start, so it belongs to this run and must attach now.
        scheduler.ScheduleDeferred(new SpeedAttachRequest(
            "game",
            100,
            1.5,
            logPath,
            DateTime.UtcNow.AddMinutes(-5),
            @"D:\Games\Game\Game.exe"));

        await WaitForConditionAsync(() => applied.Count > 0, TimeSpan.FromSeconds(3));
        scheduler.CancelAll();
        Assert.Single(applied);
    }

    [Fact]
    public async Task ProcessGone_SkipsAttachCompletely()
    {
        var probe = new StubIdentityProbe(returnFound: false);
        var applied = new ConcurrentQueue<SpeedAttachRequest>();
        var scheduler = new SpeedAttachScheduler(
            TimeProvider.System,
            NullLogger.Instance,
            applied.Enqueue,
            probe,
            PollInterval,
            TimeSpan.FromMilliseconds(100));

        scheduler.ScheduleDeferred(new SpeedAttachRequest("game", 100, 1.5, null, null, @"D:\Games\Game\Game.exe"));

        // Wait past the fallback: without the identity guard this request would attach.
        await Task.Delay(800);
        scheduler.CancelAll();
        Assert.Empty(applied);
    }

    [Fact]
    public async Task PidReusedByAnotherImage_SkipsAttach()
    {
        var probe = new StubIdentityProbe(returnFound: true, imagePath: @"C:\Elsewhere\Other.exe");
        var applied = new ConcurrentQueue<SpeedAttachRequest>();
        var scheduler = new SpeedAttachScheduler(
            TimeProvider.System,
            NullLogger.Instance,
            applied.Enqueue,
            probe,
            PollInterval,
            TimeSpan.FromMilliseconds(100));

        scheduler.ScheduleDeferred(new SpeedAttachRequest("game", 100, 1.5, null, null, @"D:\Games\Game\Game.exe"));

        // Wait past the fallback: without the identity guard this request would attach.
        await Task.Delay(800);
        scheduler.CancelAll();
        Assert.Empty(applied);
    }

    [Fact]
    public async Task CancelPending_StopsTheDeferredAttach()
    {
        var applied = new List<SpeedAttachRequest>();
        var scheduler = new SpeedAttachScheduler(
            TimeProvider.System,
            NullLogger.Instance,
            applied.Add,
            pollInterval: PollInterval,
            timeout: TimeSpan.FromSeconds(30));

        scheduler.ScheduleDeferred(new SpeedAttachRequest("game", 100, 1.5, null, null, null));
        await Task.Delay(50);
        Assert.True(scheduler.CancelPending(100));
        await Task.Delay(800);

        scheduler.CancelAll();
        Assert.Empty(applied);
    }

    [Fact]
    public void ChainloaderMarker_IsVisibleWhileTheGameHoldsTheLogOpen()
    {
        var logPath = Path.Combine(_tempDirectory, "BepInEx", "LogOutput.log");
        Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);
        File.WriteAllText(logPath, "booting\n[Message:   BepInEx] Chainloader startup complete\n");

        // Reproduce the game: BepInEx keeps a write handle open on the log for the whole session.
        // A File.ReadAllText(FileShare.Read) request conflicts with this live writer, which is how
        // the gate silently degraded to the full timeout on every attach.
        using var gameWriter = new FileStream(logPath, FileMode.Open, FileAccess.Write, FileShare.Read);
        Assert.Throws<IOException>(() => File.ReadAllText(logPath));

        var gateStartedBeforeWrite = DateTime.UtcNow.AddMinutes(-5);
        Assert.True(GameHelper.Core.Services.SpeedAttachScheduler.TryChainloaderComplete(
            logPath, gateStartedBeforeWrite));

        // Freshness stays enforced: a floor after the last write must not satisfy the gate.
        Assert.False(GameHelper.Core.Services.SpeedAttachScheduler.TryChainloaderComplete(
            logPath, DateTime.UtcNow.AddMinutes(1)));
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDirectory))
            {
                Directory.Delete(_tempDirectory, recursive: true);
            }
        }
        catch
        {
            // Best-effort test cleanup.
        }
    }

    private sealed class StubIdentityProbe : IProcessIdentityProbe
    {
        private readonly bool _returnFound;
        private readonly string? _imagePath;

        public StubIdentityProbe(bool returnFound, string? imagePath = null)
        {
            _returnFound = returnFound;
            _imagePath = imagePath;
        }

        public bool TryGetImagePath(int processId, out string? imagePath)
        {
            imagePath = _returnFound ? _imagePath : null;
            return _returnFound;
        }
    }

    private static async Task WaitForConditionAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(25);
        }

        Assert.True(condition(), $"Condition was not met within {timeout}.");
    }
}