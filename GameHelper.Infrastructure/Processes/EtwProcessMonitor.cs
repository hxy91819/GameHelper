using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using System.Threading;
using GameHelper.Core.Abstractions;
using GameHelper.Core.Models;

using GameHelper.Infrastructure.Exceptions;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Diagnostics.Tracing.Session;
using Microsoft.Extensions.Logging;

namespace GameHelper.Infrastructure.Processes
{
    /// <summary>
    /// ETW-based process monitor that provides low-latency process lifecycle notifications.
    /// Requires administrator privileges to access kernel ETW providers.
    /// </summary>
    public sealed class EtwProcessMonitor : IProcessMonitor, IStopEventsControl, IDisposable
    {
        private TraceEventSession? _session;
        private Thread? _processingThread;
        private volatile bool _isRunning;
        private volatile bool _stopEventsEnabled = true;
        private volatile bool _disposed;
        private readonly HashSet<string> _allowedProcessNames;
        private readonly ILogger<EtwProcessMonitor>? _logger;
        private readonly string _sessionName;

    private readonly ConcurrentDictionary<int, string> _startPathCache = new();

        /// <inheritdoc />
        public event Action<ProcessEventInfo>? ProcessStarted;
        /// <inheritdoc />
        public event Action<ProcessEventInfo>? ProcessStopped;

        /// <summary>
        /// Creates a new ETW process monitor with optional process filtering.
        /// </summary>
        /// <param name="allowedProcessNames">Optional whitelist of process names to monitor. If null, monitors all processes.</param>
        /// <param name="logger">Optional logger for diagnostic information.</param>
        public EtwProcessMonitor(IEnumerable<string>? allowedProcessNames = null, ILogger<EtwProcessMonitor>? logger = null)
        {
            _logger = logger;
            _sessionName = $"GameHelper-ETW-{Guid.NewGuid():N}";
            
            // Normalize and store allowed process names
            _allowedProcessNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (allowedProcessNames != null)
            {
                foreach (var name in allowedProcessNames)
                {
                    if (!string.IsNullOrWhiteSpace(name))
                    {
                        var normalized = NormalizeProcessName(name);
                        _allowedProcessNames.Add(normalized);
                    }
                }
            }

            _logger?.LogDebug("EtwProcessMonitor created with {Count} allowed processes", _allowedProcessNames.Count);
        }

        /// <inheritdoc />
        public void Start()
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(EtwProcessMonitor));

            if (_isRunning)
            {
                _logger?.LogDebug("ETW monitor already running");
                return;
            }

            if (!IsRunningAsAdministrator())
            {
                throw new InsufficientPrivilegesException();
            }

            try
            {
                InitializeEtwSession();
                _isRunning = true;
                _logger?.LogInformation("ETW process monitor started successfully");
            }
            catch (Exception ex) when (IsResourceExhausted(ex))
            {
                _logger?.LogWarning(ex, "ETW session resource exhausted. Cleaning up stale sessions and retrying.");
                SafeCleanup();
                CleanupStaleSessions();
                try
                {
                    InitializeEtwSession();
                    _isRunning = true;
                    _logger?.LogInformation("ETW process monitor started successfully after cleanup");
                }
                catch (Exception retryEx)
                {
                    _logger?.LogError(retryEx, "Failed to start ETW monitor even after cleanup");
                    SafeCleanup();
                    throw new EtwMonitorException("Failed to initialize ETW session after cleanup", retryEx);
                }
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Failed to start ETW process monitor");
                SafeCleanup();
                throw new EtwMonitorException("Failed to initialize ETW session", ex);
            }
        }

        /// <inheritdoc />
        public void Stop()
        {
            if (_disposed || !_isRunning)
                return;

            _logger?.LogDebug("Stopping ETW process monitor");
            SafeCleanup();
            _logger?.LogInformation("ETW process monitor stopped");
        }

        /// <inheritdoc />
        public void SetStopEventsEnabled(bool enabled)
        {
            if (_stopEventsEnabled != enabled)
            {
                _stopEventsEnabled = enabled;
                if (enabled)
                {
                    _startPathCache.Clear();
                    _logger?.LogDebug("ETW stop events re-enabled; path cache cleared");
                }
            }
            _logger?.LogDebug("ETW stop events enabled: {Enabled}", enabled);
        }

        /// <inheritdoc />
        public void Dispose()
        {
            if (_disposed)
                return;

            _disposed = true;
            SafeCleanup();
            _logger?.LogDebug("EtwProcessMonitor disposed");
        }

        private void InitializeEtwSession()
        {
            _session = new TraceEventSession(_sessionName);
            
            // Enable kernel process events
            _session.EnableKernelProvider(KernelTraceEventParser.Keywords.Process);

            // Set up event handlers
            _session.Source.Kernel.ProcessStart += OnProcessStart;
            _session.Source.Kernel.ProcessStop += OnProcessStop;

            // Start processing events in a background thread
            _processingThread = new Thread(ProcessEvents)
            {
                Name = "ETW-ProcessMonitor",
                IsBackground = true
            };
            _processingThread.Start();
        }

        private void ProcessEvents()
        {
            try
            {
                _logger?.LogDebug("ETW event processing thread started");
                _session?.Source.Process();
            }
            catch (Exception ex)
            {
                if (!_disposed && _isRunning)
                {
                    _logger?.LogError(ex, "ETW event processing thread encountered an error");
                }
            }
            finally
            {
                _logger?.LogDebug("ETW event processing thread ended");
            }
        }

        private void OnProcessStart(TraceEvent data)
        {
            try
            {
                var processName = GetProcessName(data);
                if (string.IsNullOrWhiteSpace(processName))
                    return;

                if (IsAllowedProcess(processName))
                {
                    var imageFileName = data.PayloadByName("ImageFileName") as string;
                    var livePath = GetRealProcessPath(data.ProcessID);
                    var realPath = livePath ?? imageFileName;
                    var cached = false;
                    if (!string.IsNullOrWhiteSpace(realPath) && _stopEventsEnabled)
                    {
                        _startPathCache[data.ProcessID] = realPath;
                        cached = true;
                    }

                    _logger?.LogDebug(
                        "Process started: {ProcessName} (PID: {ProcessId}, ImageFileName={ImageFileName}, LivePath={LivePath}, Cached={Cached})",
                        processName, data.ProcessID, imageFileName, livePath, cached);

                    var info = new ProcessEventInfo(processName, realPath);
                    ProcessStarted?.Invoke(info);
                }
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Error processing ProcessStart event");
            }
        }

        private void OnProcessStop(TraceEvent data)
        {
            if (!_stopEventsEnabled)
                return;

            try
            {
                var processName = GetProcessName(data);
                if (string.IsNullOrWhiteSpace(processName))
                    return;

                if (IsAllowedProcess(processName))
                {
                    var cacheHit = _startPathCache.TryRemove(data.ProcessID, out var realPath);
                    var fallbackImageFileName = data.PayloadByName("ImageFileName") as string;
                    if (!cacheHit)
                    {
                        realPath = fallbackImageFileName;
                    }

                    _logger?.LogDebug(
                        "Process stopped: {ProcessName} (PID: {ProcessId}, CacheHit={CacheHit}, CachedPath={CachedPath}, Fallback={Fallback})",
                        processName, data.ProcessID, cacheHit, realPath, fallbackImageFileName);

                    var info = new ProcessEventInfo(processName, realPath);
                    ProcessStopped?.Invoke(info);
                }
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Error processing ProcessStop event");
            }
        }

        private static string? GetProcessName(TraceEvent data)
        {
            // Try to get the image file name from the event
            if (data.PayloadByName("ImageFileName") is string imageFileName && !string.IsNullOrWhiteSpace(imageFileName))
            {
                try
                {
                    return Path.GetFileName(imageFileName);
                }
                catch
                {
                    // Fall back to process name if path parsing fails
                }
            }

            // Fall back to process name
            if (data.PayloadByName("ProcessName") is string processName && !string.IsNullOrWhiteSpace(processName))
            {
                return processName;
            }

            return null;
        }

        private bool IsAllowedProcess(string processName)
        {
            // If no whitelist is configured, allow all processes
            if (_allowedProcessNames.Count == 0)
                return true;

            var normalized = NormalizeProcessName(processName);
            return _allowedProcessNames.Contains(normalized);
        }

        private static string NormalizeProcessName(string processName)
        {
            if (string.IsNullOrWhiteSpace(processName))
                return processName;

            // Keep filename only and ensure .exe suffix for consistency
            var name = Path.GetFileName(processName.Trim());
            if (!name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            {
                name += ".exe";
            }
            return name;
        }

        /// <summary>
        /// Checks whether an exception indicates ETW session resource exhaustion (0x800705AA).
        /// </summary>
        private static bool IsResourceExhausted(Exception ex)
        {
            // 0x800705AA = ERROR_NO_SYSTEM_RESOURCES
            return ex is COMException comEx
                && unchecked((uint)comEx.HResult) == 0x800705AA;
        }

        /// <summary>
        /// Finds and stops any stale GameHelper ETW sessions left behind by previous
        /// process crashes or test interruptions.
        /// </summary>
        private void CleanupStaleSessions()
        {
            try
            {
                var staleSessions = TraceEventSession.GetActiveSessionNames()
                    .Where(name => name.StartsWith("GameHelper-ETW-", StringComparison.OrdinalIgnoreCase))
                    .ToList();

                if (staleSessions.Count == 0)
                {
                    _logger?.LogDebug("No stale GameHelper ETW sessions found");
                    return;
                }

                _logger?.LogWarning("Found {Count} stale GameHelper ETW sessions, cleaning up", staleSessions.Count);

                foreach (var name in staleSessions)
                {
                    try
                    {
                        using var session = new TraceEventSession(name);
                        session.Stop();
                        _logger?.LogDebug("Stopped stale ETW session: {SessionName}", name);
                    }
                    catch (Exception stopEx)
                    {
                        _logger?.LogDebug(stopEx, "Failed to stop stale ETW session: {SessionName}", name);
                    }
                }
            }
            catch (Exception cleanupEx)
            {
                _logger?.LogDebug(cleanupEx, "Error during stale ETW session cleanup");
            }
        }

        /// <summary>
        /// Gets the real executable path for a process, resolving inaccurate paths from shortcut launches.
        /// </summary>
        /// <param name="processId">Process ID</param>
        /// <returns>The real executable path, or null if retrieval fails.</returns>
        private string? GetRealProcessPath(int processId)
        {
            try
            {
                using var process = Process.GetProcessById(processId);
                return QueryFullProcessImageName(process.Handle);
            }
            catch (Exception ex)
            {
                // Process may have already exited, or insufficient privileges.
                _logger?.LogDebug(ex, "Unable to get real path for process {ProcessId}", processId);
                return null;
            }
        }

        /// <summary>
        /// Uses Win32 API QueryFullProcessImageName to obtain the full image path for a process.
        /// This method is more reliable than ETW's ImageFileName for correctly handling shortcut-launched processes.
        /// </summary>
        /// <param name="processHandle">Process handle</param>
        /// <returns>The full executable path, or null if retrieval fails.</returns>
        private static string? QueryFullProcessImageName(IntPtr processHandle)
        {
            const uint maxPath = 1024;
            uint size = maxPath;
            var sb = new StringBuilder((int)size);

            if (NativeMethods.QueryFullProcessImageName(processHandle, 0, sb, ref size))
            {
                return sb.ToString();
            }

            return null;
        }

        /// <summary>
        /// Win32 API declarations
        /// </summary>
        private static class NativeMethods
        {
            /// <summary>
            /// Retrieves the full image file name for a process.
            /// See: https://learn.microsoft.com/en-us/windows/win32/api/winbase/nf-winbase-queryfullprocessimagenamea
            /// </summary>
            [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
            [return: MarshalAs(UnmanagedType.Bool)]
            public static extern bool QueryFullProcessImageName(
                IntPtr hProcess,
                uint dwFlags,
                StringBuilder lpExeName,
                ref uint lpdwSize);
        }

        private static bool IsRunningAsAdministrator()
        {
            try
            {
                using var identity = WindowsIdentity.GetCurrent();
                var principal = new WindowsPrincipal(identity);
                return principal.IsInRole(WindowsBuiltInRole.Administrator);
            }
            catch
            {
                return false;
            }
        }

        private void SafeCleanup()
        {
            _isRunning = false;
            _startPathCache.Clear();

            // 1) Break the processing loop so the thread exits Process() first
            if (_session != null)
            {
                try
                {
                    _session.Source.StopProcessing();
                }
                catch (Exception ex)
                {
                    _logger?.LogWarning(ex, "Error stopping ETW source processing");
                }
            }

            // 2) Unsubscribe from events
            if (_session != null)
            {
                try
                {
                    _session.Source.Kernel.ProcessStart -= OnProcessStart;
                    _session.Source.Kernel.ProcessStop -= OnProcessStop;
                }
                catch (Exception ex)
                {
                    _logger?.LogWarning(ex, "Error unsubscribing from ETW events");
                }
            }

            // 3) Stop and dispose the session
            if (_session != null)
            {
                try
                {
                    _session.Stop();
                }
                catch (Exception ex)
                {
                    _logger?.LogWarning(ex, "Error stopping ETW session");
                }

                try
                {
                    _session.Dispose();
                }
                catch (Exception ex)
                {
                    _logger?.LogWarning(ex, "Error disposing ETW session");
                }

                _session = null;
            }

            // 4) Wait for the processing thread to finish
            if (_processingThread != null)
            {
                try
                {
                    if (_processingThread.IsAlive)
                    {
                        _processingThread.Join(TimeSpan.FromSeconds(5));
                    }
                }
                catch (Exception ex)
                {
                    _logger?.LogWarning(ex, "Error waiting for ETW processing thread to complete");
                }

                _processingThread = null;
            }
        }
    }
}

