using System;
using System.Collections.Concurrent;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using GameHelper.Core.Abstractions;
using GameHelper.Core.Utilities;
using Microsoft.Extensions.Logging;

namespace GameHelper.Core.Services;

/// <summary>
/// Delays a Cheat Engine attach until the game's mod loader finishes booting. Cheat Engine injects
/// helper DLLs into the game on attach; running that during BepInEx's interop generation phase
/// crashes the whole game process (observed as a c0000005 in the game with zero loader logs).
/// The gate waits for the loader's per-run log to carry its "chainloader startup complete" marker,
/// falls back to a timeout attach, and skips the attach when the pid dies or gets reused meanwhile.
/// </summary>
public sealed class SpeedAttachScheduler
{
    /// <summary>Log line BepInEx writes once its chainloader has loaded all plugins and handed control back to the game.</summary>
    internal const string ChainloaderCompleteMarker = "Chainloader startup complete";

    public static readonly TimeSpan DefaultPollInterval = TimeSpan.FromSeconds(2);
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(90);

    private readonly TimeProvider _timeProvider;
    private readonly ILogger _logger;
    private readonly Action<SpeedAttachRequest, long> _apply;
    private readonly IProcessIdentityProbe? _identityProbe;
    private readonly TimeSpan _pollInterval;
    private readonly TimeSpan _timeout;
    private readonly ConcurrentDictionary<int, CancellationTokenSource> _pending = new();
    // Multiple games may attach concurrently. Shutdown takes the exclusive side after cancellation.
    private readonly ReaderWriterLockSlim _executionBoundary = new();
    private static readonly TimeSpan StopDrainTimeout = TimeSpan.FromSeconds(5);

    public SpeedAttachScheduler(
        TimeProvider timeProvider,
        ILogger logger,
        Action<SpeedAttachRequest> apply,
        IProcessIdentityProbe? identityProbe = null,
        TimeSpan? pollInterval = null,
        TimeSpan? timeout = null)
        : this(timeProvider, logger,
            apply is null ? throw new ArgumentNullException(nameof(apply)) : (request, _) => apply(request),
            identityProbe, pollInterval, timeout)
    {
    }

    internal SpeedAttachScheduler(
        TimeProvider timeProvider,
        ILogger logger,
        Action<SpeedAttachRequest, long> apply,
        IProcessIdentityProbe? identityProbe,
        TimeSpan? pollInterval,
        TimeSpan? timeout)
    {
        _timeProvider = timeProvider ?? TimeProvider.System;
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _apply = apply ?? throw new ArgumentNullException(nameof(apply));
        _identityProbe = identityProbe;
        _pollInterval = pollInterval ?? DefaultPollInterval;
        _timeout = timeout ?? DefaultTimeout;
    }

    /// <summary>
    /// Schedules a deferred attach. The request is keyed by PID: game stop events and service
    /// shutdown cancel it via <see cref="CancelPending"/> / <see cref="CancelAll"/>.
    /// </summary>
    public void ScheduleDeferred(SpeedAttachRequest request) => ScheduleDeferred(request, 0);

    // The owner captures generation when requesting work, never when the callback resumes.
    internal void ScheduleDeferred(SpeedAttachRequest request, long generation)
    {
        var cts = new CancellationTokenSource();

        // The newest request for a PID owns it. A leftover entry means the previous launch's stop
        // event was missed and the OS recycled the PID; replacing it here keeps a relaunch on a
        // recycled PID from being silently dropped as a duplicate schedule.
        while (!_pending.TryAdd(request.ProcessId, cts))
        {
            if (_pending.TryRemove(request.ProcessId, out var stale))
            {
                CancelNoDispose(stale, request.ProcessId);
            }
        }

        _logger.LogInformation(
            "Speed attach for {DataKey} deferred until the mod loader finishes booting (PID {ProcessId}, marker timeout {Timeout}).",
            request.DataKey,
            request.ProcessId,
            _timeout);
        _ = Task.Run(() => RunGateAsync(request, generation, cts.Token, cts));
    }

    /// <summary>Cancels a pending deferred attach. Returns true when one existed for the PID.</summary>
    public bool CancelPending(int processId)
    {
        if (!_pending.TryRemove(processId, out var cts))
        {
            return false;
        }

        CancelNoDispose(cts, processId);
        return true;
    }

    /// <summary>Cancels every pending deferred attach (service shutdown).</summary>
    public void CancelAll()
    {
        foreach (var processId in _pending.Keys)
        {
            CancelPending(processId);
        }
    }

    /// <summary>
    /// Cancels queued work and waits at most five seconds for callbacks already executing.
    /// A timeout is logged and cleanup may continue: a controller call cannot safely be interrupted.
    /// </summary>
    internal void CancelAllAndWait()
    {
        CancelAll();
        try
        {
            if (!_executionBoundary.TryEnterWriteLock(StopDrainTimeout))
            {
                _logger.LogWarning("Speed attachment did not finish within the five-second shutdown limit; cleanup continues with an outstanding attachment.");
                return;
            }
            _executionBoundary.ExitWriteLock();
        }
        catch (LockRecursionException ex)
        {
            _logger.LogWarning(ex, "Cannot drain speed attachment from inside its own callback; cleanup continues with an outstanding attachment.");
        }
    }

    /// <summary>
    /// Signals the gate to stop. Disposal is owned by <see cref="RunGateAsync"/>'s finally block, so
    /// a cancel can never race a dispose while the gate is still awaiting on the same token.
    /// </summary>
    private void CancelNoDispose(CancellationTokenSource cts, int processId)
    {
        try
        {
            cts.Cancel();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to cancel the pending speed attach for PID {ProcessId}.", processId);
        }
    }

    private async Task RunGateAsync(SpeedAttachRequest request, long generation, CancellationToken cancellationToken, CancellationTokenSource cts)
    {
        try
        {
            var startedTimestamp = _timeProvider.GetTimestamp();
            var startedWallClockUtc = _timeProvider.GetUtcNow().UtcDateTime;

            // A loader can finish booting before the gate starts (fast boot / slow start-event
            // delivery). The process start is the honest freshness baseline: anything written to
            // the log after it belongs to this run, anything older is a previous run's leftover.
            var logMustBeNewerThanUtc = request.ProcessStartedUtc ?? startedWallClockUtc;

            while (true)
            {
                await Task.Delay(_pollInterval, _timeProvider, cancellationToken).ConfigureAwait(false);

                if (HasProcessChanged(request))
                {
                    return;
                }

                var elapsed = _timeProvider.GetElapsedTime(startedTimestamp);
                if (TryChainloaderComplete(request.ChainloaderLogPath, logMustBeNewerThanUtc))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    _logger.LogInformation(
                        "Mod loader for {DataKey} finished booting after {Elapsed}; attaching speed now (PID {ProcessId}).",
                        request.DataKey,
                        elapsed,
                        request.ProcessId);
                    InvokeApply(request, generation, cancellationToken);
                    return;
                }

                if (elapsed >= _timeout)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    _logger.LogInformation(
                        "Mod loader for {DataKey} did not report completion within {Timeout}; attaching speed as the timeout fallback (PID {ProcessId}).",
                        request.DataKey,
                        _timeout,
                        request.ProcessId);
                    InvokeApply(request, generation, cancellationToken);
                    return;
                }
            }
        }
        catch (OperationCanceledException)
        {
            _logger.LogDebug("Deferred speed attach cancelled for {DataKey} (PID {ProcessId}).", request.DataKey, request.ProcessId);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Speed attach gate for {DataKey} failed; speed not applied (PID {ProcessId}).",
                request.DataKey,
                request.ProcessId);
        }
        finally
        {
            _pending.TryRemove(new KeyValuePair<int, CancellationTokenSource>(request.ProcessId, cts));
            try { cts.Dispose(); }
            catch (Exception ex) { _logger.LogDebug(ex, "Failed to dispose the finished speed attach source for PID {ProcessId}.", request.ProcessId); }
        }
    }

    private void InvokeApply(SpeedAttachRequest request, long generation, CancellationToken cancellationToken)
    {
        _executionBoundary.EnterReadLock();
        try
        {
            // Cancellation may have happened after the gate's last check (including in logging).
            // Stop takes the write side, so a successful drain covers apply and record publication.
            cancellationToken.ThrowIfCancellationRequested();
            try { _apply(request, generation); }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Deferred speed apply failed for {DataKey} (PID {ProcessId}).", request.DataKey, request.ProcessId);
            }
        }
        finally
        {
            _executionBoundary.ExitReadLock();
        }
    }

    /// <summary>Detects the target process disappearing or the OS recycling the PID for another image.</summary>
    private bool HasProcessChanged(SpeedAttachRequest request)
    {
        var probe = _identityProbe;
        if (probe is null || request.ExpectedProcessPath is null)
        {
            return false;
        }

        if (!probe.TryGetImagePath(request.ProcessId, out var imagePath) || string.IsNullOrWhiteSpace(imagePath))
        {
            _logger.LogInformation(
                "Process {ProcessId} for {DataKey} is gone; skipping the deferred speed attach.",
                request.ProcessId,
                request.DataKey);
            return true;
        }

        var normalizedImage = PathNormalizer.NormalizePath(imagePath);
        var normalizedExpected = PathNormalizer.NormalizePath(request.ExpectedProcessPath);
        if (normalizedImage is not null
            && normalizedExpected is not null
            && !string.Equals(normalizedImage, normalizedExpected, StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogWarning(
                "PID {ProcessId} now belongs to {Image}, not the expected {Expected}; skipping the deferred speed attach for {DataKey} (PID reuse guard).",
                request.ProcessId,
                imagePath,
                request.ExpectedProcessPath,
                request.DataKey);
            return true;
        }

        return false;
    }

    /// <summary>
    /// The loader log is rewritten on every launch, so the marker only counts when the file was
    /// written after <paramref name="logMustBeNewerThanUtc"/> (the game process start): a stale
    /// previous-run log must not satisfy the gate, while a fast boot that finished before the gate
    /// started must.
    /// </summary>
    internal static bool TryChainloaderComplete(string? logPath, DateTime logMustBeNewerThanUtc)
    {
        if (string.IsNullOrWhiteSpace(logPath))
        {
            return false;
        }

        try
        {
            if (!File.Exists(logPath) || File.GetLastWriteTimeUtc(logPath) < logMustBeNewerThanUtc)
            {
                return false;
            }

            // The game keeps its loader log open with a write handle for the whole session. A
            // default File.ReadAllText(FileShare.Read) request conflicts with that live writer and
            // throws on every poll, so the marker would never be seen and every attach would fall
            // back to the full timeout (observed: CE attach always ~90 s late). Share with the
            // writer explicitly instead.
            using var log = new FileStream(
                logPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(log);
            return reader.ReadToEnd().Contains(ChainloaderCompleteMarker, StringComparison.Ordinal);
        }
        catch (Exception)
        {
            // The game is likely mid-write; the next poll retries.
            return false;
        }
    }
}
