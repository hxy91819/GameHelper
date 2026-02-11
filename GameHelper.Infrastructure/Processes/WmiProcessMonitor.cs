using System;
using System.Management;
using GameHelper.Core.Abstractions;
using GameHelper.Core.Models;

namespace GameHelper.Infrastructure.Processes
{
    /// <summary>
    /// Windows-only process monitor based on WMI (Win32_ProcessStartTrace/StopTrace).
    /// Raises simple string events with the process executable name (e.g., "game.exe").
    /// Internally resolves names via ProcessID against Win32_Process to avoid truncated names.
    /// </summary>
    public sealed class WmiProcessMonitor : IProcessMonitor, IDisposable, IStopEventsControl
    {
        private IProcessEventWatcher? _startWatcher;
        private IProcessEventWatcher? _stopWatcher;
        private bool _ownsWatchers;
        private bool _running;
        private bool _disposed;
        private readonly string? _startWql; // keep null to use full StartTrace query (see ctor)
        private readonly string? _stopWql;  // keep null to use full StopTrace query (see ctor)
        private bool _stopEventsEnabled = true; // default to true for backward compatibility

        /// <inheritdoc />
        public event Action<ProcessEventInfo>? ProcessStarted;
        /// <inheritdoc />
        public event Action<ProcessEventInfo>? ProcessStopped;

        // Default ctor uses real WMI watchers without any filtering (listen to all processes)
        public WmiProcessMonitor() { }

        // Test-friendly ctor accepts external watchers
        public WmiProcessMonitor(IProcessEventWatcher startWatcher, IProcessEventWatcher stopWatcher)
        {
            _startWatcher = startWatcher;
            _stopWatcher = stopWatcher;
            _ownsWatchers = false;
        }

        /// <summary>
        /// Enables or disables emitting ProcessStopped events by starting/stopping the Stop watcher.
        /// Start events are always enabled.
        /// </summary>
        public void SetStopEventsEnabled(bool enabled)
        {
            _stopEventsEnabled = enabled;
            if (_disposed) return;
            if (_stopWatcher is null) return; // will be honored when Start() creates watchers
            if (!_running) return; // will be honored upon Start()

            try
            {
                if (enabled)
                {
                    _stopWatcher.Start();
                }
                else
                {
                    _stopWatcher.Stop();
                }
            }
            catch { }
        }

        /// <summary>
        /// Creates a monitor that filters start/stop events to a whitelist of process names.
        /// If <paramref name="allowedProcessNames"/> is null or empty, falls back to listening to all processes.
        /// </summary>
        public WmiProcessMonitor(System.Collections.Generic.IEnumerable<string>? allowedProcessNames)
        {
            if (allowedProcessNames is not null)
            {
                var list = new System.Collections.Generic.List<string>();
                foreach (var n in allowedProcessNames)
                {
                    if (string.IsNullOrWhiteSpace(n)) continue;
                    // Ensure we only keep a file name (defensive) and quote it for WQL
                    var name = System.IO.Path.GetFileName(n.Trim());
                    // WQL string literal uses double quotes; escape embedded quotes if any
                    name = name.Replace("\"", "\\\"");
                    list.Add(name);
                }

                // Intentionally do NOT apply name whitelist at WQL level anymore.
                // Reason: Start/Stop may report different names (e.g., launcher vs. target, x86/x64 variants),
                // causing Start to be missed if filtered. We now listen to all and filter in GameAutomationService.
                _startWql = null;
                _stopWql  = null;
            }
        }

        /// <summary>
        /// Starts underlying WMI watchers and begins emitting events.
        /// </summary>
        public void Start()
        {
            if (_disposed) throw new ObjectDisposedException(nameof(WmiProcessMonitor));
            if (_running) return;

            try
            {
                if (_startWatcher is null || _stopWatcher is null)
                {
                    var startQuery = _startWql ?? "SELECT * FROM Win32_ProcessStartTrace";
                    var stopQuery  = _stopWql  ?? "SELECT * FROM Win32_ProcessStopTrace";
                    _startWatcher = new WmiEventWatcher(startQuery);
                    _stopWatcher  = new WmiEventWatcher(stopQuery);
                    _ownsWatchers = true;
                }

                _startWatcher.ProcessEvent += OnStartEvent;
                _stopWatcher.ProcessEvent += OnStopEvent;

                _startWatcher.Start();
                if (_stopEventsEnabled)
                {
                    _stopWatcher.Start();
                }

                _running = true;
            }
            catch
            {
                SafeTearDown();
                throw;
            }
        }

        /// <summary>
        /// Stops watchers and unsubscribes internal handlers.
        /// </summary>
        public void Stop()
        {
            if (_disposed) return;
            if (!_running) return;
            SafeTearDown();
        }

        private void OnStartEvent(ProcessEventInfo processInfo)
        {
            if (string.IsNullOrWhiteSpace(processInfo.ExecutableName))
            {
                return;
            }

            ProcessStarted?.Invoke(processInfo);
        }

        private void OnStopEvent(ProcessEventInfo processInfo)
        {
            if (string.IsNullOrWhiteSpace(processInfo.ExecutableName))
            {
                return;
            }

            ProcessStopped?.Invoke(processInfo);
        }

        private void SafeTearDown()
        {
            _running = false;

            if (_startWatcher is not null)
            {
                try { _startWatcher.ProcessEvent -= OnStartEvent; } catch { }
                try { _startWatcher.Stop(); } catch { }
                if (_ownsWatchers && _startWatcher is IDisposable d1) d1.Dispose();
                _startWatcher = null;
            }

            if (_stopWatcher is not null)
            {
                try { _stopWatcher.ProcessEvent -= OnStopEvent; } catch { }
                try { _stopWatcher.Stop(); } catch { }
                if (_ownsWatchers && _stopWatcher is IDisposable d2) d2.Dispose();
                _stopWatcher = null;
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            SafeTearDown();
        }
    }

    /// <summary>
    /// Thin wrapper over <see cref="ManagementEventWatcher"/> with a simplified event signature.
    /// Uses the ProcessID from received events to query <c>Win32_Process</c> and resolve a stable executable name.
    /// </summary>
    internal sealed class WmiEventWatcher : IProcessEventWatcher, IDisposable
    {
        private readonly ManagementEventWatcher _watcher;
        private bool _started;
        // Keep a short-lived cache of PID -> resolved executable name captured on Start events
        private readonly System.Collections.Generic.Dictionary<int, string> _pidToName = new();

        /// <inheritdoc />
        public event Action<ProcessEventInfo>? ProcessEvent;

        /// <summary>
        /// Creates a watcher for the specified WQL query (e.g., StartTrace/StopTrace).
        /// </summary>
        public WmiEventWatcher(string wqlQuery)
        {
            _watcher = new ManagementEventWatcher(new WqlEventQuery(wqlQuery));
            _watcher.EventArrived += OnEventArrived;
        }

        /// <summary>
        /// Starts listening for events. Idempotent.
        /// </summary>
        public void Start()
        {
            if (_started) return;
            _watcher.Start();
            _started = true;
        }

        /// <summary>
        /// Stops listening for events. Safe to call if not started.
        /// </summary>
        public void Stop()
        {
            if (!_started) return;
            try { _watcher.Stop(); } catch { }
            _started = false;
        }

        private void OnEventArrived(object sender, EventArrivedEventArgs e)
        {
            try
            {
                var newEvent = e.NewEvent;
                if (newEvent is null)
                {
                    return;
                }

                string? resolvedName = null;
                string? resolvedPath = null;
                int pid = -1;
                string? className = null;

                // Determine event class name first
                try { className = newEvent.ClassPath?.ClassName; } catch { }

                // Extract PID if available
                try
                {
                    var pidObj = newEvent["ProcessID"]; // available on Win32_ProcessStartTrace/StopTrace
                    if (pidObj is not null)
                    {
                        pid = Convert.ToInt32(pidObj);
                        // Only perform expensive WMI lookup for Start (or unknown) events to reduce CPU usage
                        var isStop = className?.IndexOf("Stop", StringComparison.OrdinalIgnoreCase) >= 0;
                        if (!isStop)
                        {
                            // Optimization: Try System.Diagnostics.Process first (faster)
                            try
                            {
                                using var proc = System.Diagnostics.Process.GetProcessById(pid);
                                var fullPath = proc.MainModule?.FileName;
                                if (!string.IsNullOrWhiteSpace(fullPath))
                                {
                                    resolvedPath = fullPath;
                                    resolvedName = System.IO.Path.GetFileName(fullPath);
                                }
                            }
                            catch
                            {
                                // Fallback to WMI if access denied or process exited
                            }

                            if (string.IsNullOrWhiteSpace(resolvedName) || string.IsNullOrWhiteSpace(resolvedPath))
                            {
                                using var searcher = new ManagementObjectSearcher($"SELECT Name, ExecutablePath FROM Win32_Process WHERE ProcessId = {pid}");
                                using var results = searcher.Get();
                                foreach (ManagementObject proc in results)
                                {
                                    // Prefer Name; if missing, take filename from ExecutablePath
                                    resolvedName = proc["Name"] as string;
                                    if (string.IsNullOrWhiteSpace(resolvedName))
                                    {
                                        resolvedPath = proc["ExecutablePath"] as string;
                                        if (!string.IsNullOrWhiteSpace(resolvedPath))
                                        {
                                            try { resolvedName = System.IO.Path.GetFileName(resolvedPath); } catch { }
                                        }
                                    }
                                    else
                                    {
                                        resolvedPath = proc["ExecutablePath"] as string;
                                    }
                                    break; // first match
                                }
                            }
                        }
                    }
                }
                catch
                {
                    // swallow and fallback to ProcessName
                }

                if (string.IsNullOrWhiteSpace(resolvedName))
                {
                    // If this is a Stop event and process is already gone, reuse the cached name from Start
                    if (pid > 0 && (className?.IndexOf("Stop", StringComparison.OrdinalIgnoreCase) >= 0))
                    {
                        if (_pidToName.TryGetValue(pid, out var cached))
                        {
                            resolvedName = cached;
                        }
                    }

                    // Still null? Fall back to the raw ProcessName provided by the event
                    if (string.IsNullOrWhiteSpace(resolvedName))
                    {
                        resolvedName = newEvent["ProcessName"]?.ToString();
                    }
                }

                if (!string.IsNullOrWhiteSpace(resolvedName))
                {
                    // Maintain cache lifecycle: add on Start, remove on Stop
                    if (pid > 0)
                    {
                        if (className?.IndexOf("Start", StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            _pidToName[pid] = resolvedName;
                        }
                        else if (className?.IndexOf("Stop", StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            _pidToName.Remove(pid);
                        }
                    }

                    var info = new ProcessEventInfo(resolvedName, resolvedPath);
                    ProcessEvent?.Invoke(info);
                }
            }
            catch
            {
                // ignore
            }
        }

        public void Dispose()
        {
            try { _watcher.EventArrived -= OnEventArrived; } catch { }
            try { if (_started) _watcher.Stop(); } catch { }
            _watcher.Dispose();
        }
    }
}
