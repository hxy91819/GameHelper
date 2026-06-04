using GameHelper.Core.Abstractions;

namespace GameHelper.Core.Services;

public sealed class MonitorControlService : IMonitorControlService
{
    private readonly IProcessMonitor _monitor;
    private readonly IGameAutomationService _automationService;

    public MonitorControlService(IProcessMonitor monitor, IGameAutomationService automationService)
    {
        _monitor = monitor;
        _automationService = automationService;
    }

    public bool IsRunning { get; private set; }

    public void Start()
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
    }

    public void Stop()
    {
        if (!IsRunning)
        {
            return;
        }

        try
        {
            _monitor.Stop();
            _automationService.Stop();
        }
        catch
        {
            try
            {
                _automationService.Stop();
            }
            catch
            {
                // Best-effort cleanup.
            }

            throw;
        }
        finally
        {
            IsRunning = false;
        }
    }
}
