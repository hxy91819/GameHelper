using System.Diagnostics;
using System.Security.Principal;
using GameHelper.Core.Models;
using GameHelper.Infrastructure.Exceptions;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Diagnostics.Tracing.Session;
using Microsoft.Extensions.Logging;
using GameHelper.Core.Abstractions;

namespace GameHelper.Infrastructure.Processes;

internal sealed class EtwRuntime(ILogger<EtwProcessMonitor>? logger) : IEtwRuntime
{
    public void EnsureElevated()
    {
        using var identity = WindowsIdentity.GetCurrent();
        if (!new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator))
            throw new InsufficientPrivilegesException();
    }

    public IEtwSession CreateSession(string name) => new Session(name, logger);
    public IReadOnlyCollection<string> GetActiveSessionNames() => TraceEventSession.GetActiveSessionNames().ToArray();
    public void StopSession(string name)
    {
        using var session = new TraceEventSession(name, TraceEventSessionOptions.Attach);
        session.Stop();
    }

    public ProcessProbe InspectProcess(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return process.HasExited ? new(true, null) : new(false, process.StartTime.ToUniversalTime());
        }
        catch (ArgumentException) { return new(true, null); }
        catch (InvalidOperationException) { return new(true, null); }
        catch (Exception ex)
        {
            logger?.LogDebug(ex, "Unable to inspect identity of PID {ProcessId}", processId);
            return new(false, null);
        }
    }

    public IReadOnlyList<EtwProcessEvent> CaptureExistingProcesses(ProcessObservationPolicy policy)
    {
        var result = new List<EtwProcessEvent>();
        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                try
                {
                    var name = process.ProcessName + ".exe";
                    if (!policy.Includes(name)) continue;
                    // Path enrichment is optional: name-only games can still be adopted when
                    // Windows denies access to the executable's module information.
                    string? path = null;
                    try { path = process.MainModule?.FileName; }
                    catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or NotSupportedException)
                    {
                        logger?.LogDebug(ex, "Cannot resolve path of running process {ProcessId}", process.Id);
                    }
                    // Take the observation time before the final liveness check. An exit after
                    // that check must not appear older than this synthetic start event.
                    var observedAt = DateTime.UtcNow;
                    if (process.HasExited) continue;
                    result.Add(new(new ProcessEventInfo(name, path, process.Id), observedAt));
                }
                catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or NotSupportedException)
                {
                    logger?.LogDebug(ex, "Cannot capture running process {ProcessId}", process.Id);
                }
            }
        }
        return result;
    }

    public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken) => Task.Delay(delay, cancellationToken);

    private sealed class Session(string name, ILogger<EtwProcessMonitor>? logger) : IEtwSession
    {
        private TraceEventSession? _trace;
        private Thread? _thread;
        private volatile bool _disposed;
        public bool IsAlive => !_disposed && _thread?.IsAlive == true;

        public void Start(Action<EtwProcessEvent> started, Action<EtwProcessEvent> stopped, Action ended)
        {
            // Retain partial initialization so Dispose also releases a failed provider startup.
            var trace = new TraceEventSession(name);
            _trace = trace;
            trace.EnableKernelProvider(KernelTraceEventParser.Keywords.Process);
            var source = trace.Source;
            source.Kernel.ProcessStart += data => Dispatch(data, started);
            source.Kernel.ProcessStop += data => Dispatch(data, stopped);
            _thread = new Thread(() =>
            {
                try { source.Process(); }
                catch (Exception ex)
                {
                    if (!_disposed) logger?.LogWarning(ex, "ETW event processing failed");
                }
                finally { ended(); }
            }) { Name = "ETW-ProcessMonitor", IsBackground = true };
            _thread.Start();
        }

        private void Dispatch(TraceEvent data, Action<EtwProcessEvent> callback)
        {
            try
            {
                var path = data.PayloadByName("ImageFileName") as string;
                var processName = string.IsNullOrWhiteSpace(path)
                    ? data.PayloadByName("ProcessName") as string : Path.GetFileName(path);
                callback(new(new ProcessEventInfo(processName ?? "", path, data.ProcessID), data.TimeStamp.ToUniversalTime()));
            }
            catch (Exception ex) { logger?.LogWarning(ex, "Error dispatching ETW process event"); }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            var trace = _trace;
            _trace = null;
            if (trace is null) return;
            try { trace.Source.StopProcessing(); }
            catch (Exception ex) { logger?.LogWarning(ex, "Error stopping ETW source processing"); }
            try { trace.Stop(); }
            catch (Exception ex) { logger?.LogWarning(ex, "Error stopping ETW session"); }
            try { trace.Dispose(); }
            catch (Exception ex) { logger?.LogWarning(ex, "Error disposing ETW session"); }
            // Do not join under the monitor's lifecycle lock. Late callbacks are rejected by
            // session identity; the reader owns its captured source until Process returns.
        }
    }
}
