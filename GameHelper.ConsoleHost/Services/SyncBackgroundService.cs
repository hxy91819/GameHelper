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
    private readonly object _sync = new();

    private CancellationTokenSource? _cts;
    private Task? _loopTask;
    private bool _started;

    public SyncBackgroundService(ISyncService syncService, ILogger<SyncBackgroundService> logger)
    {
        _syncService = syncService;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        lock (_sync)
        {
            if (_started)
            {
                return Task.CompletedTask;
            }

            _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _loopTask = Task.Run(() => RunLoopAsync(_cts.Token), CancellationToken.None);
            _started = true;
            _logger.LogInformation(
                "统计自动推送已启动（首次检查延迟 {InitialDelay}，检查周期 {CheckInterval}）",
                InitialDelay,
                CheckInterval);
            return Task.CompletedTask;
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        CancellationTokenSource? cts;
        Task? loopTask;
        lock (_sync)
        {
            if (!_started)
            {
                return;
            }

            _started = false;
            cts = _cts;
            loopTask = _loopTask;
        }

        if (cts is not null)
        {
            await cts.CancelAsync().ConfigureAwait(false);
        }

        if (loopTask is not null)
        {
            try
            {
                await loopTask.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "统计推送循环停止时出现非致命错误");
            }
        }

        lock (_sync)
        {
            _cts?.Dispose();
            _cts = null;
            _loopTask = null;
        }
    }

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
                _logger.LogError(ex, "统计推送循环出现未预期错误");
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
                _logger.LogInformation("统计自动推送完成：{CommitId}", outcome.CommitId ?? "(unknown)");
                break;
            case SyncOutcomeStatus.Skipped:
                _logger.LogDebug("统计自动推送跳过：{Detail}", outcome.Detail);
                break;
            case SyncOutcomeStatus.Failed:
                _logger.LogWarning("统计自动推送失败：{Error}", outcome.Error);
                break;
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_started)
            {
                // 宿主未调用 StopAsync 直接释放时的兜底：取消循环，但不等待。
                _cts?.Cancel();
                _started = false;
            }

            _cts?.Dispose();
            _cts = null;
            _loopTask = null;
        }
    }
}
