using System;
using System.Management;
using GameHelper.Core.Abstractions;

namespace GameHelper.Infrastructure.Processes
{
    // Windows-only: monitors process start/stop via WMI and raises simple string events with the process name (e.g., "game.exe")
    public sealed class WmiProcessMonitor : IProcessMonitor, IDisposable
    {
        private IProcessEventWatcher? _startWatcher;
        private IProcessEventWatcher? _stopWatcher;
        private bool _ownsWatchers;
        private bool _running;
        private bool _disposed;

        public event Action<string>? ProcessStarted;
        public event Action<string>? ProcessStopped;

        // Default ctor uses real WMI watchers
        public WmiProcessMonitor() { }

        // Test-friendly ctor accepts external watchers
        public WmiProcessMonitor(IProcessEventWatcher startWatcher, IProcessEventWatcher stopWatcher)
        {
            _startWatcher = startWatcher;
            _stopWatcher = stopWatcher;
            _ownsWatchers = false;
        }

        public void Start()
        {
            if (_disposed) throw new ObjectDisposedException(nameof(WmiProcessMonitor));
            if (_running) return;

            try
            {
                if (_startWatcher is null || _stopWatcher is null)
                {
                    _startWatcher = new WmiEventWatcher("SELECT * FROM Win32_ProcessStartTrace");
                    _stopWatcher = new WmiEventWatcher("SELECT * FROM Win32_ProcessStopTrace");
                    _ownsWatchers = true;
                }

                _startWatcher.ProcessEvent += OnStartEvent;
                _stopWatcher.ProcessEvent += OnStopEvent;

                _startWatcher.Start();
                _stopWatcher.Start();

                _running = true;
            }
            catch
            {
                SafeTearDown();
                throw;
            }
        }

        public void Stop()
        {
            if (_disposed) return;
            if (!_running) return;
            SafeTearDown();
        }

        private void OnStartEvent(string processName)
        {
            if (!string.IsNullOrWhiteSpace(processName))
                ProcessStarted?.Invoke(processName);
        }

        private void OnStopEvent(string processName)
        {
            if (!string.IsNullOrWhiteSpace(processName))
                ProcessStopped?.Invoke(processName);
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

    internal sealed class WmiEventWatcher : IProcessEventWatcher, IDisposable
    {
        private readonly ManagementEventWatcher _watcher;
        private bool _started;

        public event Action<string>? ProcessEvent;

        public WmiEventWatcher(string wqlQuery)
        {
            _watcher = new ManagementEventWatcher(new WqlEventQuery(wqlQuery));
            _watcher.EventArrived += OnEventArrived;
        }

        public void Start()
        {
            if (_started) return;
            _watcher.Start();
            _started = true;
        }

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
                var name = e.NewEvent["ProcessName"]?.ToString();
                if (!string.IsNullOrWhiteSpace(name))
                    ProcessEvent?.Invoke(name);
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
