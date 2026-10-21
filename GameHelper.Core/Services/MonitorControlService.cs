using GameHelper.Core.Abstractions;

namespace GameHelper.Core.Services;

public sealed class MonitorControlService : IMonitorControlService
{
    private readonly IProcessMonitor _monitor;
    private readonly IGameAutomationService _automationService;
    private readonly IConfigWatcher? _configWatcher;
    private bool _configWatcherStarted;
    private readonly object _gate = new();
    private bool _isRunning;

    public MonitorControlService(IProcessMonitor monitor, IGameAutomationService automationService,
        IConfigWatcher? configWatcher = null)
    {
        _monitor = monitor;
        _automationService = automationService;
        _configWatcher = configWatcher;
    }

    public bool IsRunning { get { lock (_gate) return _isRunning; } private set { _isRunning = value; } }

    public void Start()
    {
        lock (_gate) StartCore();
    }

    private void StartCore()
    {
        if (IsRunning)
        {
            return;
        }

        bool automationStarted = false;
        try
        {
            _automationService.Start();
            automationStarted = true;
            _monitor.Start();
            IsRunning = true;
        }
        catch
        {
            if (automationStarted)
            {
                try
                {
                    _automationService.Stop();
                }
                catch
                {
                    // Best-effort rollback to prevent duplicate event subscriptions.
                }
            }

            throw;
        }

        // Config hot reload is auxiliary: it must never put monitoring state at risk.
        // The watcher's lifetime follows the host process, not monitor Stop(), so a
        // stopped monitor can still pick up config edits and resume cleanly later.
        if (!_configWatcherStarted)
        {
            try
            {
                _configWatcher?.Start();
            }
            catch
            {
                // Watcher failures are logged inside the implementation; hot reload degrades to a no-op.
            }

            _configWatcherStarted = true;
        }
    }

    public void Stop()
    {
        lock (_gate) StopCore();
    }

    private void StopCore()
    {
        if (!IsRunning)
        {
            return;
        }

        try
        {
            _monitor.Stop();
            _automationService.Stop();
            IsRunning = false;
        }
        catch
        {
            try
            {
                _monitor.Stop();
            }
            catch
            {
                // Best-effort cleanup.
            }

            try
            {
                _automationService.Stop();
            }
            catch
            {
                // Best-effort cleanup.
            }

            // Services have been stopped (even if with errors); reflect actual state.
            IsRunning = false;
            throw;
        }
    }
}
