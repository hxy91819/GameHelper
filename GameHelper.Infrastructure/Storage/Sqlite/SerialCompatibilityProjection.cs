using System.Threading.Channels;

namespace GameHelper.Infrastructure.Storage.Sqlite;

/// <summary>
/// Owns full legacy projection on one worker. Admission never performs I/O or calls back synchronously;
/// its worker never acquires automation locks. Explicit flush/drain is only allowed outside those locks.
/// </summary>
internal sealed class SerialCompatibilityProjection : IDisposable
{
    private readonly Channel<(TaskCompletionSource? Completion, bool Full)> _work = Channel.CreateUnbounded<(TaskCompletionSource?, bool)>(
        new UnboundedChannelOptions { SingleReader = true, AllowSynchronousContinuations = false });
    private readonly Action _export;
    private readonly Action _exportPending;
    private readonly Action<Exception> _reportFailure;
    private readonly Task _worker;
    private int _pending;

    internal SerialCompatibilityProjection(Action export, Action<Exception> reportFailure, Action? exportPending = null)
    {
        _export = export;
        _exportPending = exportPending ?? export;
        _reportFailure = reportFailure;
        _worker = Task.Run(RunAsync);
    }

    internal void Request()
    {
        if (Interlocked.CompareExchange(ref _pending, 1, 0) != 0) return;
        if (!_work.Writer.TryWrite((null, false))) Interlocked.Exchange(ref _pending, 0);
    }

    internal Task FlushAsync(bool full = true)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_work.Writer.TryWrite((completion, full))) completion.SetException(new ObjectDisposedException(nameof(SerialCompatibilityProjection)));
        return completion.Task;
    }

    // Synchronous snapshot/sync boundary; the worker never captures the caller's synchronization context.
    internal void Flush() => FlushAsync().GetAwaiter().GetResult();

    private async Task RunAsync()
    {
        await foreach (var (completion, full) in _work.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            if (completion is null && !full) Interlocked.Exchange(ref _pending, 0);
            try { if (full) _export(); else _exportPending(); completion?.SetResult(); }
            catch (Exception ex) { _reportFailure(ex); completion?.SetException(ex); }
        }
    }

    public void Dispose()
    {
        _work.Writer.TryWrite((null, true)); // One complete handoff before closing admission.
        _work.Writer.TryComplete();
        _worker.GetAwaiter().GetResult();
    }
}
