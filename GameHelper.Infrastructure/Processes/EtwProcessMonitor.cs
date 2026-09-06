using System.Globalization;
using System.Runtime.InteropServices;
using GameHelper.Core.Abstractions;
using GameHelper.Core.Models;
using GameHelper.Infrastructure.Exceptions;
using Microsoft.Extensions.Logging;

namespace GameHelper.Infrastructure.Processes;

/// <summary>ETW process observation with isolated session ownership and bounded recovery.</summary>
public sealed class EtwProcessMonitor : IProcessMonitor
{
    private const string SessionNamePrefix = "GameHelper-ETW-";
    /// <summary>Maximum consecutive attempts to rebuild a lost session.</summary>
    public const int SessionRecoveryAttempts = 3;
    private readonly object _lifecycleLock = new();
    private readonly IEtwRuntime _runtime;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<EtwProcessMonitor>? _logger;
    private readonly Dictionary<int, EtwProcessEvent> _observedProcesses = new();
    private ProcessObservationPolicy _policy = ProcessObservationPolicy.ObserveAll();
    private IEtwSession? _session;
    private string _sessionName = "";
    private bool _isRunning;
    private bool _disposed;
    private bool _stopRequested = true;
    private bool _recovering;
    private long _generation;
    private CancellationTokenSource? _runCancellation;
    private ITimer? _healthCheckTimer;
    private Task _recoveryTask = Task.CompletedTask;

    /// <summary>Creates a monitor with optional candidate names and diagnostics.</summary>
    public EtwProcessMonitor(IEnumerable<string>? allowedProcessNames = null, ILogger<EtwProcessMonitor>? logger = null)
        : this(new EtwRuntime(logger), allowedProcessNames, logger) { }

    internal EtwProcessMonitor(IEtwRuntime runtime, IEnumerable<string>? allowedProcessNames = null,
        ILogger<EtwProcessMonitor>? logger = null, TimeProvider? timeProvider = null)
    {
        _runtime = runtime;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _logger = logger;
        if (allowedProcessNames is not null) Configure(new ProcessObservationPolicy(allowedProcessNames));
    }

    /// <inheritdoc />
    public event Action<ProcessEventInfo>? ProcessStarted;
    /// <inheritdoc />
    public event Action<ProcessEventInfo>? ProcessStopped;

    internal Task RecoveryTask { get { lock (_lifecycleLock) return _recoveryTask; } }

    /// <inheritdoc />
    public void Start()
    {
        lock (_lifecycleLock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_stopRequested && (_isRunning || _recovering)) return;
            _runtime.EnsureElevated();
            CancelRun();
            SafeCleanup();
            _recovering = false;
            _runCancellation = new CancellationTokenSource();
            _stopRequested = false;
            var generation = ++_generation;
            try
            {
                try { InitializeEtwSession(generation); }
                catch (Exception ex) when (IsResourceExhausted(ex))
                {
                    SafeCleanup();
                    CleanupStaleSessions();
                    InitializeEtwSession(generation);
                }
                _healthCheckTimer = _timeProvider.CreateTimer(_ => CheckSessionHealth(generation), null,
                    TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));
                _logger?.LogInformation("ETW process monitor started successfully");
            }
            catch (Exception ex)
            {
                _stopRequested = true;
                CancelRun();
                SafeCleanup();
                throw new EtwMonitorException("Failed to initialize ETW session", ex);
            }
        }
    }

    /// <inheritdoc />
    public void Stop()
    {
        lock (_lifecycleLock)
        {
            _stopRequested = true;
            ++_generation;
            _recovering = false;
            CancelRun();
            SafeCleanup();
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        lock (_lifecycleLock)
        {
            if (_disposed) return;
            _disposed = true;
            Stop();
        }
    }

    /// <inheritdoc />
    public void Configure(ProcessObservationPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        Volatile.Write(ref _policy, policy);
    }

    private void CancelRun()
    {
        _healthCheckTimer?.Dispose();
        _healthCheckTimer = null;
        _runCancellation?.Cancel();
        _runCancellation?.Dispose();
        _runCancellation = null;
    }

    private bool IsCurrent(long generation) => !_disposed && !_stopRequested && generation == _generation;

    private void InitializeEtwSession(long generation)
    {
        // Every assignment follows cleanup, including partial initialization.
        SafeCleanup(preserveProcesses: true);
        _sessionName = $"{SessionNamePrefix}{Environment.ProcessId}-{Guid.NewGuid():N}";
        var session = _runtime.CreateSession(_sessionName);
        _session = session;
        _isRunning = true;
        session.Start(
            data => Dispatch(session, generation, () => OnProcessStart(data)),
            data => Dispatch(session, generation, () => OnProcessStop(data)),
            () => Dispatch(session, generation, () => ScheduleRecovery(generation)));
    }

    private void Dispatch(IEtwSession session, long generation, Action callback)
    {
        lock (_lifecycleLock)
        {
            if (!IsCurrent(generation) || !ReferenceEquals(session, _session)) return;
            try { callback(); }
            catch (Exception ex) { _logger?.LogWarning(ex, "Error processing ETW notification"); }
        }
    }

    private void CheckSessionHealth(long generation)
    {
        lock (_lifecycleLock)
        {
            if (!IsCurrent(generation) || _recovering || !_isRunning) return;
            try
            {
                if (_session?.IsAlive != true ||
                    !_runtime.GetActiveSessionNames().Contains(_sessionName, StringComparer.OrdinalIgnoreCase))
                    ScheduleRecovery(generation);
                else ReconcileObservedProcesses();
            }
            catch (Exception ex) { _logger?.LogWarning(ex, "ETW session health check failed"); }
        }
    }

    private void ScheduleRecovery(long generation)
    {
        if (!IsCurrent(generation) || _recovering) return;
        _recovering = true;
        var token = _runCancellation!.Token;
        _logger?.LogWarning("ETW event stream ended unexpectedly; scheduling session recovery");
        _recoveryTask = Task.Run(() => RunSessionRecoveryAsync(generation, token));
    }

    private async Task RunSessionRecoveryAsync(long generation, CancellationToken cancellationToken)
    {
        try
        {
            for (var attempt = 1; attempt <= SessionRecoveryAttempts; attempt++)
            {
                lock (_lifecycleLock)
                {
                    if (!IsCurrent(generation)) return;
                    SafeCleanup(preserveProcesses: true);
                    ReconcileObservedProcesses();
                }
                await _runtime.DelayAsync(TimeSpan.FromSeconds(attempt), cancellationToken).ConfigureAwait(false);
                lock (_lifecycleLock)
                {
                    if (!IsCurrent(generation)) return;
                    try
                    {
                        InitializeEtwSession(generation);
                        ReconcileObservedProcesses();
                        if (!IsCurrent(generation)) return;
                        if (_session?.IsAlive != true)
                            throw new InvalidOperationException("Replacement ETW reader has already ended");
                        _logger?.LogInformation("ETW session recovered on attempt {Attempt}; process monitoring resumed", attempt);
                        return;
                    }
                    catch (Exception ex)
                    {
                        // Clean immediately, including the final failed attempt.
                        SafeCleanup(preserveProcesses: true);
                        _logger?.LogWarning(ex, "ETW session recovery attempt {Attempt}/{Attempts} failed", attempt, SessionRecoveryAttempts);
                    }
                }
            }
            lock (_lifecycleLock)
            {
                if (!IsCurrent(generation)) return;
                ReconcileObservedProcesses();
                _stopRequested = true;
                CancelRun();
                _logger?.LogError("ETW session recovery failed after {Attempts} attempts; process monitoring is DOWN. Restart monitoring to resume.", SessionRecoveryAttempts);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception ex)
        {
            lock (_lifecycleLock)
            {
                if (IsCurrent(generation))
                {
                    SafeCleanup(preserveProcesses: true);
                    _stopRequested = true;
                    CancelRun();
                    _logger?.LogError(ex, "ETW recovery aborted; restart monitoring to resume");
                }
            }
        }
        finally
        {
            lock (_lifecycleLock)
            {
                if (generation == _generation) _recovering = false;
            }
        }
    }

    private void OnProcessStart(EtwProcessEvent data)
    {
        if (data.Info.ProcessId is not int pid) return;
        if (_observedProcesses.TryGetValue(pid, out var previous))
        {
            if (data.TimestampUtc <= previous.TimestampUtc) return;
            CompleteObservedProcess(pid, previous, reconciled: true);
        }
        if (!IsAllowedProcessStart(data.Info.ExecutableName, data.Info.ExecutablePath)) return;
        _observedProcesses[pid] = data;
        ProcessStarted?.Invoke(data.Info);
    }

    private void OnProcessStop(EtwProcessEvent data)
    {
        if (data.Info.ProcessId is not int pid || !_observedProcesses.TryGetValue(pid, out var previous)) return;
        // A queued stop from before reconciliation/PID reuse cannot end the new incarnation.
        if (data.TimestampUtc < previous.TimestampUtc) return;
        CompleteObservedProcess(pid, previous, reconciled: false);
    }

    private void CompleteObservedProcess(int pid, EtwProcessEvent previous, bool reconciled)
    {
        _observedProcesses.Remove(pid);
        if (!IsAllowedProcessStop(previous.Info.ExecutableName, hadCachedStart: true)) return;
        if (reconciled)
            _logger?.LogWarning("Reconciling missing process stop for PID {ProcessId}; exact exit time unavailable, playtime ends at reconciliation time", pid);
        try { ProcessStopped?.Invoke(previous.Info); }
        catch (Exception ex) { _logger?.LogWarning(ex, "Error delivering process stop for PID {ProcessId}", pid); }
    }

    private void ReconcileObservedProcesses()
    {
        foreach (var (pid, previous) in _observedProcesses.ToArray())
        {
            var process = _runtime.InspectProcess(pid);
            if (process.HasExited || process.StartTimeUtc > previous.TimestampUtc)
                CompleteObservedProcess(pid, previous, reconciled: true);
        }
    }

    internal bool IsAllowedProcessStart(string processName, string? pathHint)
    {
        var policy = CurrentPolicy;
        if (policy.Includes(processName)) return true;
        if (string.IsNullOrWhiteSpace(pathHint)) return false;
        try { return policy.Includes(Path.GetFileName(pathHint)); }
        catch (ArgumentException) { return false; }
    }

    internal bool IsAllowedProcessStop(string? processName, bool hadCachedStart) =>
        CurrentPolicy.ObserveStopEvents && !string.IsNullOrWhiteSpace(processName) &&
        (hadCachedStart || CurrentPolicy.Includes(processName));

    private ProcessObservationPolicy CurrentPolicy => Volatile.Read(ref _policy);
    private static bool IsResourceExhausted(Exception ex) =>
        ex is COMException && unchecked((uint)ex.HResult) == 0x800705AA;

    private void CleanupStaleSessions()
    {
        try
        {
            foreach (var name in _runtime.GetActiveSessionNames())
            {
                if (!ShouldCleanupSession(name, pid => !_runtime.InspectProcess(pid).HasExited)) continue;
                try { _runtime.StopSession(name); }
                catch (Exception ex) { _logger?.LogWarning(ex, "Unable to clean stale ETW session {SessionName}", name); }
            }
        }
        catch (Exception ex) { _logger?.LogWarning(ex, "Unable to enumerate stale ETW sessions"); }
    }

    internal static bool ShouldCleanupSession(string sessionName, Func<int, bool> isProcessAlive)
    {
        ArgumentNullException.ThrowIfNull(isProcessAlive);
        if (!sessionName.StartsWith(SessionNamePrefix, StringComparison.OrdinalIgnoreCase)) return false;
        var remainder = sessionName[SessionNamePrefix.Length..];
        var separator = remainder.IndexOf('-');
        // Legacy/unknown names may belong to a live old instance; leave them alone.
        if (separator <= 0 || !int.TryParse(remainder[..separator], NumberStyles.None,
                CultureInfo.InvariantCulture, out var ownerPid) || ownerPid <= 0 ||
            !Guid.TryParseExact(remainder[(separator + 1)..], "N", out _)) return false;
        return !isProcessAlive(ownerPid);
    }

    private void SafeCleanup(bool preserveProcesses = false)
    {
        _isRunning = false;
        var session = _session;
        _session = null;
        try { session?.Dispose(); }
        catch (Exception ex) { _logger?.LogWarning(ex, "Error cleaning up ETW session"); }
        if (!preserveProcesses)
        {
            _observedProcesses.Clear();
        }
    }
}
