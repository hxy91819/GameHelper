using System;
using System.Threading;
using System.Threading.Tasks;
using GameHelper.Core.Abstractions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace GameHelper.ConsoleHost.Services;

/// <summary>
/// 统计自动推送后台循环：启动延迟数分钟后首次检查，之后按固定周期轻量检查
/// （配置读取 + 文件 mtime 对比），由 <see cref="ISyncService.SyncNowAsync"/> 决定是否真正上传。
/// Start/Stop 状态对称：Start 幂等，Stop 等待循环退出并释放资源。
/// </summary>
internal sealed class SyncBackgroundService : IHostedService, IDisposable
{
    /// <summary>启动后首次检查的延迟：避开监控启动高峰，也给用户留出修改配置的时间。</summary>
    public static readonly TimeSpan InitialDelay = TimeSpan.FromMinutes(3);

    /// <summary>常规检查周期。检查本身只做 mtime 对比，开销可忽略。</summary>
    public static readonly TimeSpan CheckInterval = TimeSpan.FromMinutes(15);

    private readonly ISyncService _syncService;
    private readonly ILogger<SyncBackgroundService> _logger;
    private readonly BackgroundLoopLifecycle _lifecycle;

    public SyncBackgroundService(ISyncService syncService, ILogger<SyncBackgroundService> logger)
    {
        _syncService = syncService;
        _logger = logger;
        _lifecycle = new BackgroundLoopLifecycle(RunLoopAsync, logger);
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (_lifecycle.Start(cancellationToken))
            _logger.LogInformation("Stats sync loop started (first check delay {InitialDelay}, check interval {CheckInterval})", InitialDelay, CheckInterval);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => _lifecycle.StopAsync(cancellationToken);

    private async Task RunLoopAsync(CancellationToken cancellationToken)
    {
        var firstIteration = true;
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var delay = firstIteration ? InitialDelay : CheckInterval;
                firstIteration = false;
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
                await RunOnceAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unexpected error in the stats sync loop");
                try
                {
                    await Task.Delay(TimeSpan.FromMinutes(1), cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }
    }

    private async Task RunOnceAsync(CancellationToken cancellationToken)
    {
        var outcome = await _syncService.SyncNowAsync(force: false, cancellationToken).ConfigureAwait(false);
        switch (outcome.Status)
        {
            case SyncOutcomeStatus.Uploaded:
                _logger.LogInformation("Stats sync completed: {CommitId}", outcome.CommitId ?? "(unknown)");
                break;
            case SyncOutcomeStatus.Skipped:
                _logger.LogDebug("Stats sync skipped: {Detail}", outcome.Detail);
                break;
            case SyncOutcomeStatus.Failed:
                _logger.LogWarning("Stats sync failed: {Error}", outcome.Error);
                break;
        }
    }

    public void Dispose() => _lifecycle.Dispose();
}
