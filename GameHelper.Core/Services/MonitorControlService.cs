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

        _automationService.Start();
        _monitor.Start();
        IsRunning = true;
    }

    public void Stop()
    {
        if (!IsRunning)
        {
            return;
        }

        _monitor.Stop();
        _automationService.Stop();
        IsRunning = false;
    }
}
