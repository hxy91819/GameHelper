using System;
using System.Collections.Concurrent;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using GameHelper.Core.Abstractions;
using GameHelper.Core.Utilities;
using Microsoft.Extensions.Logging;

namespace GameHelper.Core.Services;

/// <summary>Deferred speed attach request produced when a mod loader was detected at game start.</summary>
/// <param name="DataKey">Game config key, for logging only.</param>
/// <param name="ProcessId">Game process the speed applies to.</param>
/// <param name="Speed">Configured speed multiplier.</param>
/// <param name="ChainloaderLogPath">Loader log rewritten per launch; completion marker source. Null disables the marker check (timeout-only gating).</param>
/// <param name="ExpectedProcessPath">Executable path the PID must still own at attach time; guards against PID reuse during the wait.</param>
public sealed record SpeedAttachRequest(
    string DataKey,
    int ProcessId,
    double Speed,
    string? ChainloaderLogPath,
    string? ExpectedProcessPath);

/// <summary>
/// Delays a Cheat Engine attach until the game's mod loader finishes booting. Cheat Engine injects
/// helper DLLs into the game on attach; running that during BepInEx's interop generation phase
/// crashes the whole game process (observed as a c0000005 in the game with zero loader logs).
/// The gate waits for the loader's per-run log to carry its "chainloader startup complete" marker,
/// falls back to a timeout attach, and skips the attach when the pid dies or gets reused meanwhile.
/// </summary>
public sealed class SpeedAttachScheduler
{
    internal const string ChainloaderCompleteMarker = "Chainloader startup complete";

    /// <summary>Log line BepInEx writes once its chainloader has loaded all plugins and handed control back to the game.</summary>
    public static readonly TimeSpan DefaultPollInterval = TimeSpan.FromSeconds(2);
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(90);

    private readonly TimeProvider _timeProvider;
    private readonly ILogger _logger;
    private readonly Action<SpeedAttachRequest> _apply;
    private readonly IProcessIdentityProbe? _identityProbe;
    private readonly TimeSpan _pollInterval;
    private readonly TimeSpan _timeout;
    private readonly ConcurrentDictionary<int, CancellationTokenSource> _pending = new();

    public SpeedAttachScheduler(
        TimeProvider timeProvider,
        ILogger logger,
        Action<SpeedAttachRequest> apply,
        IProcessIdentityProbe? identityProbe = null,
        TimeSpan? pollInterval = null,
        TimeSpan? timeout = null)
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
    public void ScheduleDeferred(SpeedAttachRequest request)
    {
        var cts = new CancellationTokenSource();
        if (!_pending.TryAdd(request.ProcessId, cts))
        {
            cts.Dispose();
            _logger.LogDebug(
                "Speed attach is already pending for PID {ProcessId}; ignoring the duplicate schedule for {DataKey}.",
                request.ProcessId,
                request.DataKey);
            return;
        }

        _logger.LogInformation(
            "Speed attach for {DataKey} deferred until the mod loader finishes booting (PID {ProcessId}, marker timeout {Timeout}).",
            request.DataKey,
            request.ProcessId,
            _timeout);
        _ = Task.Run(() => RunGateAsync(request, cts.Token, cts));
    }

    /// <summary>Cancels a pending deferred attach. Returns true when one existed for the PID.</summary>
    public bool CancelPending(int processId)
    {
        if (!_pending.TryRemove(processId, out var cts))
        {
            return false;
        }

        try { cts.Cancel(); }
        catch (Exception ex) { _logger.LogDebug(ex, "Failed to cancel the pending speed attach for PID {ProcessId}.", processId); }
        try { cts.Dispose(); }
        catch (Exception ex) { _logger.LogDebug(ex, "Failed to dispose the pending speed attach source for PID {ProcessId}.", processId); }
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

    private async Task RunGateAsync(SpeedAttachRequest request, CancellationToken cancellationToken, CancellationTokenSource cts)
    {
        try
        {
            var startedTimestamp = _timeProvider.GetTimestamp();
            var startedWallClockUtc = _timeProvider.GetUtcNow().UtcDateTime;

            while (true)
            {
                await Task.Delay(_pollInterval, _timeProvider, cancellationToken).ConfigureAwait(false);

                if (HasProcessChanged(request))
                {
                    return;
                }

                var elapsed = _timeProvider.GetElapsedTime(startedTimestamp);
                if (TryChainloaderComplete(request.ChainloaderLogPath, startedWallClockUtc))
                {
                    _logger.LogInformation(
                        "Mod loader for {DataKey} finished booting after {Elapsed}; attaching speed now (PID {ProcessId}).",
                        request.DataKey,
                        elapsed,
                        request.ProcessId);
                    InvokeApply(request);
                    return;
                }

                if (elapsed >= _timeout)
                {
                    _logger.LogInformation(
                        "Mod loader for {DataKey} did not report completion within {Timeout}; attaching speed as the timeout fallback (PID {ProcessId}).",
                        request.DataKey,
                        _timeout,
                        request.ProcessId);
                    InvokeApply(request);
                    return;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Cancelled by a stop event or service shutdown: intentionally do not attach.
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
            CleanupPending(request.ProcessId, cts);
        }
    }

    private void InvokeApply(SpeedAttachRequest request)
    {
        try
        {
            _apply(request);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Deferred speed apply failed for {DataKey} (PID {ProcessId}).", request.DataKey, request.ProcessId);
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
    /// touched after the gate started (a stale previous-run log must not satisfy the gate).
    /// </summary>
    internal static bool TryChainloaderComplete(string? logPath, DateTime startedWallClockUtc)
    {
        if (string.IsNullOrWhiteSpace(logPath))
        {
            return false;
        }

        try
        {
            if (!File.Exists(logPath) || File.GetLastWriteTimeUtc(logPath) < startedWallClockUtc)
            {
                return false;
            }

            return File.ReadAllText(logPath).Contains(ChainloaderCompleteMarker, StringComparison.Ordinal);
        }
        catch (Exception)
        {
            // The game is likely mid-write; the next poll retries.
            return false;
        }
    }

    private void CleanupPending(int processId, CancellationTokenSource cts)
    {
        if (_pending.TryGetValue(processId, out var current)
            && ReferenceEquals(current, cts)
            && _pending.TryRemove(new KeyValuePair<int, CancellationTokenSource>(processId, cts)))
        {
            try { cts.Dispose(); }
            catch (Exception ex) { _logger.LogDebug(ex, "Failed to dispose the finished speed attach source for PID {ProcessId}.", processId); }
        }
    }
}