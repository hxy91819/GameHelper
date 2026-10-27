using System.Threading.Channels;
using GameHelper.Core.Models;
using Microsoft.Extensions.Logging;

namespace GameHelper.Core.Services;

/// <summary>
/// Serializes after-exit process I/O without borrowing the companion lock. All mutable
/// lifecycle/admission state borrows the owner's state lock; no new lock order is added.
/// Admission registers an executing lease under the same lock as Stop's invalidation.
/// Stop drains the captured worker outside every owner lock, including the check-to-call
/// window; admitted I/O may outlive a drain timeout, but cannot admit a following action.
/// A restarted consumer awaits its predecessor, so even a timed-out old call stays serial.
/// </summary>
internal sealed class AfterExitActionQueue
{
    private static readonly TimeSpan DrainTimeout = TimeSpan.FromSeconds(5);
    private readonly object _stateLock;
    private readonly Func<long, bool> _isCurrent;
    private readonly Action<GameAction> _execute;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger _logger;
    private Run? _current;
    private Task _tail = Task.CompletedTask;
    private int _executingThreadId;

    internal AfterExitActionQueue(object stateLock, Func<long, bool> isCurrent,
        Action<GameAction> execute, TimeProvider timeProvider, ILogger logger)
    {
        _stateLock = stateLock;
        _isCurrent = isCurrent;
        _execute = execute;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    internal void Start(long generation)
    {
        lock (_stateLock)
        {
            var run = new Run(generation);
            _current = run;
            var previous = _tail;
            _tail = Task.Run(() => ConsumeAsync(previous, run));
        }
    }

    internal void Enqueue(IEnumerable<GameAction> actions, long generation)
    {
        lock (_stateLock)
        {
            if (_current is not { } run || run.Generation != generation || !_isCurrent(generation)) return;
            foreach (var action in actions) run.Queue.Writer.TryWrite(action);
        }
    }

    // Called with the owner's state lock during Stop, before any best-effort cleanup.
    internal DrainRequest Stop()
    {
        lock (_stateLock)
        {
            var run = _current;
            _current = null;
            if (run is not null) Cleanup(() => run.Queue.Writer.TryComplete(), "complete the after-exit queue");
            return new(_tail, _executingThreadId == Environment.CurrentManagedThreadId);
        }
    }

    internal void Drain(DrainRequest request)
    {
        if (request.CalledFromAction)
        {
            _logger.LogWarning("Cannot drain after-exit actions from inside their own callback; cleanup continues with an outstanding action.");
            return;
        }
        try
        {
            request.Completion.WaitAsync(DrainTimeout, _timeProvider).GetAwaiter().GetResult();
        }
        catch (TimeoutException)
        {
            _logger.LogWarning("After-exit action did not finish within the five-second shutdown limit; cleanup continues with an outstanding action.");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to drain after-exit actions; cleanup continues.");
        }
    }

    private async Task ConsumeAsync(Task previous, Run run)
    {
        try
        {
            await previous.ConfigureAwait(false);
            await foreach (var action in run.Queue.Reader.ReadAllAsync().ConfigureAwait(false))
            {
                lock (_stateLock)
                {
                    if (_current != run || !_isCurrent(run.Generation)) return;
                    // This lease is part of the worker task captured by Stop, not merely a
                    // running-state check. Never hold the admission lock across process I/O.
                    _executingThreadId = Environment.CurrentManagedThreadId;
                }
                try { _execute(action); }
                catch (Exception ex) { _logger.LogWarning(ex, "After-exit action failed: {Path}", action.Path); }
                finally
                {
                    lock (_stateLock) _executingThreadId = 0;
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "After-exit consumer failed; queued actions are discarded.");
        }
        finally
        {
            // Channels have no disposable OS resource. Complete publication and release
            // queued references independently, even after an unexpected consumer failure.
            Cleanup(() => run.Queue.Writer.TryComplete(), "complete the finished after-exit queue");
            Cleanup(() => { while (run.Queue.Reader.TryRead(out _)) { } }, "discard pending after-exit actions");
        }
    }

    private void Cleanup(Action cleanup, string operation)
    {
        try { cleanup(); }
        catch (Exception ex) { _logger.LogWarning(ex, "Failed to {Operation}", operation); }
    }

    internal readonly record struct DrainRequest(Task Completion, bool CalledFromAction);

    private sealed class Run(long generation)
    {
        internal long Generation { get; } = generation;
        internal Channel<GameAction> Queue { get; } = Channel.CreateUnbounded<GameAction>(new()
        {
            SingleReader = true, AllowSynchronousContinuations = false
        });
    }
}
