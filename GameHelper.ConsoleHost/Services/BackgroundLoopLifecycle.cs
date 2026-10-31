using Microsoft.Extensions.Logging;

namespace GameHelper.ConsoleHost.Services;

/// <summary>
/// Owns each background run's token, completion and cleanup. Stop detaches one run;
/// late completion cannot clear a replacement. A replacement waits for its predecessor
/// so a caller abandoning Stop's wait cannot create overlapping background workers.
/// </summary>
internal sealed class BackgroundLoopLifecycle(Func<CancellationToken, Task> loop, ILogger logger) : IDisposable
{
    private readonly object _gate = new();
    private Run? _current;
    private Task _tail = Task.CompletedTask;
    private bool _disposed;

    internal bool Start(CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_current is not null) return false;
            var run = new Run(cancellationToken);
            var predecessor = _tail;
            run.Completion = Task.Run(async () =>
            {
                try { await predecessor.ConfigureAwait(false); }
                catch (Exception) { /* A failed previous run must not poison a restart. */ }
                run.Token.ThrowIfCancellationRequested();
                await loop(run.Token).ConfigureAwait(false);
            });
            _current = run;
            _tail = run.Completion;
            _ = CleanupAsync(run);
            return true;
        }
    }

    internal async Task StopAsync(CancellationToken cancellationToken)
    {
        Run? run;
        lock (_gate) { run = _current; _current = null; }
        if (run is null) return;
        try
        {
            // Bound cancellation callbacks as well as the worker by the host deadline.
            await run.CancelAsync().WaitAsync(cancellationToken).ConfigureAwait(false);
            await run.Completion.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { logger.LogDebug(ex, "Background loop stopped with non-fatal error"); }
    }

    private async Task CleanupAsync(Run run)
    {
        try { await run.Completion.ConfigureAwait(false); }
        catch (OperationCanceledException) { }
        catch (Exception ex) { logger.LogDebug(ex, "Background loop completed with non-fatal error"); }
        // The CTS belongs to this run until both the worker and registered cancellation
        // callbacks have finished. Dispose never reads a replaceable owner field.
        try { await run.CancelAsync().ConfigureAwait(false); }
        catch (Exception ex) { logger.LogDebug(ex, "Background loop cancellation callback failed"); }
        run.Cancellation.Dispose();
    }

    public void Dispose()
    {
        Run? run;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            run = _current;
            _current = null;
        }
        if (run is not null) _ = run.CancelAsync();
    }

    private sealed class Run
    {
        private readonly object _gate = new();
        private Task? _cancellation;
        internal CancellationTokenSource Cancellation { get; }
        internal CancellationToken Token { get; }
        internal Task Completion { get; set; } = Task.CompletedTask;
        internal Run(CancellationToken token)
        {
            Cancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
            Token = Cancellation.Token;
        }
        internal Task CancelAsync()
        {
            lock (_gate) return _cancellation ??= Cancellation.CancelAsync();
        }
    }
}
