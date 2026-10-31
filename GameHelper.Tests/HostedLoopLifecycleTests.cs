using GameHelper.ConsoleHost.Services;
using GameHelper.Core.Abstractions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace GameHelper.Tests;

public sealed class HostedLoopLifecycleTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task StopOfOldRunCannotClearRestartedRun(bool fileDrop, bool deadlineExpires)
    {
        using var service = Create(fileDrop);
        await Start(service);
        var old = CurrentCancellation(service)!;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        using var registration = old.Token.Register(() => { entered.TrySetResult(); release.Wait(); });
        using var deadline = new CancellationTokenSource();
        var stop = ((Microsoft.Extensions.Hosting.IHostedService)service).StopAsync(deadline.Token);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await Start(service);
            if (deadlineExpires)
            {
                deadline.Cancel();
                await stop.WaitAsync(TimeSpan.FromSeconds(5));
            }
        }
        finally { release.Set(); }
        await stop;
        Assert.NotNull(CurrentCancellation(service));
        Assert.False(CurrentCancellation(service)!.IsCancellationRequested);
        await Stop(service);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DisposedServiceRejectsRestart(bool fileDrop)
    {
        var service = Create(fileDrop);
        service.Dispose();
        await Assert.ThrowsAsync<ObjectDisposedException>(() => Start(service));
    }

    private static IDisposable Create(bool fileDrop) => fileDrop
        ? new FileDropIpcServer(null!, NullLogger<FileDropIpcServer>.Instance, "gh-audit-" + Guid.NewGuid().ToString("N"))
        : new SyncBackgroundService(Mock.Of<ISyncService>(), NullLogger<SyncBackgroundService>.Instance);
    private static Task Start(IDisposable service) => ((Microsoft.Extensions.Hosting.IHostedService)service).StartAsync(default);
    private static Task Stop(IDisposable service) => ((Microsoft.Extensions.Hosting.IHostedService)service).StopAsync(default);
    private static CancellationTokenSource? CurrentCancellation(IDisposable service)
    {
        const System.Reflection.BindingFlags flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
        var lifecycle = service.GetType().GetField("_lifecycle", flags)!.GetValue(service)!;
        var run = lifecycle.GetType().GetField("_current", flags)!.GetValue(lifecycle);
        return run?.GetType().GetProperty("Cancellation", flags)!.GetValue(run) as CancellationTokenSource;
    }
}
