using GameHelper.Core.Abstractions;

namespace GameHelper.Core.Services;

public sealed class MonitorControlService : IMonitorControlService
{
    private readonly IProcessMonitor _monitor;
    private readonly IGameAutomationService _automationService;
    private readonly ISpeedControlService _speedControlService;

    public MonitorControlService(
        IProcessMonitor monitor,
        IGameAutomationService automationService,
        ISpeedControlService speedControlService)
    {
        _monitor = monitor;
        _automationService = automationService;
        _speedControlService = speedControlService;
    }

    public MonitorControlService(IProcessMonitor monitor, IGameAutomationService automationService)
        : this(monitor, automationService, new NoOpSpeedControlService())
    {
    }

    public bool IsRunning { get; private set; }

    public void Start()
    {
        if (IsRunning)
        {
            return;
        }

        _automationService.Start();
        _speedControlService.Start();
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
        _speedControlService.Stop();
        _automationService.Stop();
        IsRunning = false;
    }
}
