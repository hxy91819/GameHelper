using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Principal;
using System.Threading;
using GameHelper.Core.Abstractions;
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

        /// <inheritdoc />
        public event Action<string>? ProcessStarted;
        /// <inheritdoc />
        public event Action<string>? ProcessStopped;

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
            _stopEventsEnabled = enabled;
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
                    _logger?.LogDebug("Process started: {ProcessName} (PID: {ProcessId})", processName, data.ProcessID);
                    ProcessStarted?.Invoke(processName);
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
                    _logger?.LogDebug("Process stopped: {ProcessName} (PID: {ProcessId})", processName, data.ProcessID);
                    ProcessStopped?.Invoke(processName);
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

            // Stop the ETW session
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

            // Wait for processing thread to complete
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