using System.Threading.Channels;

namespace GameHelper.Infrastructure.Storage.Sqlite;

/// <summary>
/// Owns full legacy projection on one worker. Admission never performs I/O or calls back synchronously;
/// its worker never acquires automation locks. Explicit flush/drain is only allowed outside those locks.
/// </summary>
internal sealed class SerialCompatibilityProjection : IDisposable
{
    private readonly Channel<TaskCompletionSource?> _work = Channel.CreateUnbounded<TaskCompletionSource?>(
        new UnboundedChannelOptions { SingleReader = true, AllowSynchronousContinuations = false });
    private readonly Action _export;
    private readonly Action<Exception> _reportFailure;
    private readonly Task _worker;
    private int _pending;

    internal SerialCompatibilityProjection(Action export, Action<Exception> reportFailure)
    {
        _export = export;
        _reportFailure = reportFailure;
        _worker = Task.Run(RunAsync);
    }

    internal void Request()
    {
        if (Interlocked.CompareExchange(ref _pending, 1, 0) != 0) return;
        if (!_work.Writer.TryWrite(null)) Interlocked.Exchange(ref _pending, 0);
    }

    internal Task FlushAsync()
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_work.Writer.TryWrite(completion)) completion.SetException(new ObjectDisposedException(nameof(SerialCompatibilityProjection)));
        return completion.Task;
    }

    // Synchronous snapshot/sync boundary; the worker never captures the caller's synchronization context.
    internal void Flush() => FlushAsync().GetAwaiter().GetResult();

    private async Task RunAsync()
    {
        await foreach (var completion in _work.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            if (completion is null) Interlocked.Exchange(ref _pending, 0);
            try { _export(); completion?.SetResult(); }
            catch (Exception ex) { _reportFailure(ex); completion?.SetException(ex); }
        }
    }

    public void Dispose()
    {
        _work.Writer.TryComplete();
        _worker.GetAwaiter().GetResult();
    }
}
