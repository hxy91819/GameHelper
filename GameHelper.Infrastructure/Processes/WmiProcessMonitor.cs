using System;
using System.Management;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using GameHelper.Core.Abstractions;
using GameHelper.Core.Models;

namespace GameHelper.Infrastructure.Processes
{
    /// <summary>
    /// Windows-only process monitor based on WMI (Win32_ProcessStartTrace/StopTrace).
    /// Raises simple string events with the process executable name (e.g., "game.exe").
    /// Internally resolves names via ProcessID against Win32_Process to avoid truncated names.
    /// </summary>
    public sealed class WmiProcessMonitor : IProcessMonitor, IStopEventsControl, IProcessNameFilterControl
    {
        private const string StartTraceQuery = "SELECT * FROM Win32_ProcessStartTrace";
        private const string StopTraceQuery = "SELECT * FROM Win32_ProcessStopTrace";
        private IProcessEventWatcher? _startWatcher;
        private IProcessEventWatcher? _stopWatcher;
        private WmiProcessEventResolver? _eventResolver;
        private bool _ownsWatchers;
        private readonly bool _filterExternalWatcherEvents;
        private bool _running;
        private bool _disposed;
        private bool _stopEventsEnabled = true; // default to true for backward compatibility
        private readonly object _allowedProcessNamesLock = new();
        private readonly HashSet<string> _allowedProcessNames = new(StringComparer.OrdinalIgnoreCase);
        private volatile bool _hasProcessNameFilter;

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
            _filterExternalWatcherEvents = true;
        }

        /// <summary>
        /// Creates a monitor that filters start/stop events to a whitelist of process names.
        /// If <paramref name="allowedProcessNames"/> is null, falls back to emitting all processes.
        /// </summary>
        public WmiProcessMonitor(IEnumerable<string>? allowedProcessNames)
        {
            if (allowedProcessNames is not null)
            {
                SetAllowedProcessNames(allowedProcessNames);
            }
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
                    _eventResolver?.ClearCache();
                }
            }
            catch { }
        }

        /// <inheritdoc />
        public void SetAllowedProcessNames(IEnumerable<string> processNames)
        {
            ArgumentNullException.ThrowIfNull(processNames);

            lock (_allowedProcessNamesLock)
            {
                _allowedProcessNames.Clear();
                foreach (var name in processNames)
                {
                    var normalized = NormalizeProcessName(name);
                    if (!string.IsNullOrWhiteSpace(normalized))
                    {
                        _allowedProcessNames.Add(normalized);
                    }
                }

                _hasProcessNameFilter = true;
            }

            _eventResolver?.ClearCache();
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
                    _eventResolver = new WmiProcessEventResolver(
                        IsAllowedProcessName,
                        () => _stopEventsEnabled);
                    _startWatcher = new WmiEventWatcher(StartTraceQuery, _eventResolver);
                    _stopWatcher  = new WmiEventWatcher(StopTraceQuery, _eventResolver);
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

            if (_filterExternalWatcherEvents && !IsAllowedProcessName(processInfo.ExecutableName))
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

            if (_filterExternalWatcherEvents && !IsAllowedProcessName(processInfo.ExecutableName))
            {
                return;
            }

            ProcessStopped?.Invoke(processInfo);
        }

        private bool IsAllowedProcessName(string? processName)
        {
            if (!_hasProcessNameFilter)
            {
                return true;
            }

            var normalized = NormalizeProcessName(processName);
            if (string.IsNullOrWhiteSpace(normalized))
            {
                return false;
            }

            lock (_allowedProcessNamesLock)
            {
                return _allowedProcessNames.Contains(normalized);
            }
        }

        private static string? NormalizeProcessName(string? processName)
        {
            if (string.IsNullOrWhiteSpace(processName))
            {
                return null;
            }

            var name = Path.GetFileName(processName.Trim());
            if (string.IsNullOrWhiteSpace(name))
            {
                return null;
            }

            if (!name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            {
                name += ".exe";
            }

            return name;
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

            _eventResolver?.ClearCache();
            _eventResolver = null;
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
        private readonly WmiProcessEventResolver _eventResolver;
        private bool _started;

        /// <inheritdoc />
        public event Action<ProcessEventInfo>? ProcessEvent;

        /// <summary>
        /// Creates a watcher for the specified WQL query (e.g., StartTrace/StopTrace).
        /// </summary>
        public WmiEventWatcher(string wqlQuery)
            : this(wqlQuery, new WmiProcessEventResolver(_ => true))
        {
        }

        internal WmiEventWatcher(string wqlQuery, WmiProcessEventResolver eventResolver)
        {
            _eventResolver = eventResolver ?? throw new ArgumentNullException(nameof(eventResolver));
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

                int pid = -1;
                string? className = null;
                string? rawProcessName = null;

                // Determine event class name first
                try { className = newEvent.ClassPath?.ClassName; } catch { }
                try { rawProcessName = newEvent["ProcessName"]?.ToString(); } catch { }

                // Extract PID if available
                try
                {
                    var pidObj = newEvent["ProcessID"]; // available on Win32_ProcessStartTrace/StopTrace
                    if (pidObj is not null)
                    {
                        pid = Convert.ToInt32(pidObj);
                    }
                }
                catch
                {
                    // swallow and fallback to ProcessName without a PID
                }

                if (_eventResolver.TryCreateProcessEvent(className, pid, rawProcessName, out var info))
                {
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

    internal sealed class WmiProcessEventResolver
    {
        private readonly Func<string?, bool> _isAllowedProcessName;
        private readonly Func<bool> _areStopEventsEnabled;
        private readonly Func<int, WmiProcessDetails?> _resolveProcessDetails;
        private readonly ConcurrentDictionary<int, WmiProcessDetails> _pidToDetails = new();

        public WmiProcessEventResolver(Func<string?, bool> isAllowedProcessName)
            : this(isAllowedProcessName, () => true, ResolveProcessDetails)
        {
        }

        public WmiProcessEventResolver(
            Func<string?, bool> isAllowedProcessName,
            Func<bool> areStopEventsEnabled)
            : this(isAllowedProcessName, areStopEventsEnabled, ResolveProcessDetails)
        {
        }

        public WmiProcessEventResolver(
            Func<string?, bool> isAllowedProcessName,
            Func<int, WmiProcessDetails?> resolveProcessDetails)
            : this(isAllowedProcessName, () => true, resolveProcessDetails)
        {
        }

        public WmiProcessEventResolver(
            Func<string?, bool> isAllowedProcessName,
            Func<bool> areStopEventsEnabled,
            Func<int, WmiProcessDetails?> resolveProcessDetails)
        {
            _isAllowedProcessName = isAllowedProcessName ?? throw new ArgumentNullException(nameof(isAllowedProcessName));
            _areStopEventsEnabled = areStopEventsEnabled ?? throw new ArgumentNullException(nameof(areStopEventsEnabled));
            _resolveProcessDetails = resolveProcessDetails ?? throw new ArgumentNullException(nameof(resolveProcessDetails));
        }

        public void ClearCache() => _pidToDetails.Clear();

        public bool TryCreateProcessEvent(
            string? className,
            int processId,
            string? rawProcessName,
            out ProcessEventInfo processInfo)
        {
            var isStop = IsStopEvent(className);
            if (isStop)
            {
                return TryCreateStopEvent(processId, rawProcessName, out processInfo);
            }

            return TryCreateStartEvent(processId, rawProcessName, out processInfo);
        }

        private bool TryCreateStartEvent(int processId, string? rawProcessName, out ProcessEventInfo processInfo)
        {
            processInfo = default;

            WmiProcessDetails? resolvedDetails = null;
            var rawNameAllowed = _isAllowedProcessName(rawProcessName);
            if (!rawNameAllowed && !string.IsNullOrWhiteSpace(rawProcessName))
            {
                return false;
            }

            if (processId > 0)
            {
                resolvedDetails = _resolveProcessDetails(processId);
            }

            var executableName = ResolveExecutableName(resolvedDetails, rawProcessName);
            if (!rawNameAllowed && !_isAllowedProcessName(executableName))
            {
                return false;
            }

            if (string.IsNullOrWhiteSpace(executableName))
            {
                return false;
            }

            var details = new WmiProcessDetails(executableName, resolvedDetails?.ExecutablePath, rawProcessName);
            if (processId > 0 && _areStopEventsEnabled())
            {
                _pidToDetails[processId] = details;
            }

            processInfo = new ProcessEventInfo(executableName, details.ExecutablePath, processId > 0 ? processId : null);
            return true;
        }

        private bool TryCreateStopEvent(int processId, string? rawProcessName, out ProcessEventInfo processInfo)
        {
            processInfo = default;

            WmiProcessDetails? cachedDetails = null;
            if (processId > 0 && _pidToDetails.TryRemove(processId, out var cached))
            {
                cachedDetails = cached;
            }

            if (cachedDetails is not null &&
                (string.IsNullOrWhiteSpace(rawProcessName) ||
                 (!MatchesCachedProcessName(cachedDetails.Value, rawProcessName) &&
                  !_isAllowedProcessName(rawProcessName))))
            {
                cachedDetails = null;
            }

            var executableName = ResolveExecutableName(cachedDetails, rawProcessName);
            if (string.IsNullOrWhiteSpace(executableName))
            {
                return false;
            }

            if (cachedDetails is null && !_isAllowedProcessName(executableName))
            {
                return false;
            }

            processInfo = new ProcessEventInfo(executableName, cachedDetails?.ExecutablePath, processId > 0 ? processId : null);
            return true;
        }

        private static bool IsStopEvent(string? className) =>
            className?.IndexOf("Stop", StringComparison.OrdinalIgnoreCase) >= 0;

        private static string? ResolveExecutableName(WmiProcessDetails? details, string? rawProcessName)
        {
            if (!string.IsNullOrWhiteSpace(details?.Name))
            {
                return details.Value.Name;
            }

            if (!string.IsNullOrWhiteSpace(details?.ExecutablePath))
            {
                try
                {
                    return Path.GetFileName(details.Value.ExecutablePath);
                }
                catch
                {
                    // Fall back to the raw WMI event process name below.
                }
            }

            return rawProcessName;
        }

        private static bool MatchesCachedProcessName(WmiProcessDetails details, string rawProcessName)
        {
            return NamesEqual(details.EventName, rawProcessName) ||
                NamesEqual(details.Name, rawProcessName);
        }

        private static bool NamesEqual(string? left, string? right)
        {
            var normalizedLeft = NormalizeName(left);
            var normalizedRight = NormalizeName(right);
            return normalizedLeft is not null &&
                normalizedRight is not null &&
                string.Equals(normalizedLeft, normalizedRight, StringComparison.OrdinalIgnoreCase);
        }

        private static string? NormalizeName(string? processName)
        {
            if (string.IsNullOrWhiteSpace(processName))
            {
                return null;
            }

            var name = Path.GetFileName(processName.Trim());
            if (string.IsNullOrWhiteSpace(name))
            {
                return null;
            }

            if (!name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            {
                name += ".exe";
            }

            return name;
        }

        private static WmiProcessDetails? ResolveProcessDetails(int processId)
        {
            try
            {
                using var searcher = new ManagementObjectSearcher(
                    $"SELECT Name, ExecutablePath FROM Win32_Process WHERE ProcessId = {processId}");
                using var results = searcher.Get();
                var proc = results.Cast<ManagementBaseObject>().FirstOrDefault();
                if (proc is not null)
                {
                    return new WmiProcessDetails(
                        proc["Name"] as string,
                        proc["ExecutablePath"] as string,
                        null);
                }
            }
            catch
            {
                // Fall back to the raw ProcessName from the WMI event.
            }

            return null;
        }
    }

    internal readonly record struct WmiProcessDetails(string? Name, string? ExecutablePath, string? EventName = null);
}
