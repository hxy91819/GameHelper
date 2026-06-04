namespace GameHelper.Core.Models;

public sealed class AppSettingsSnapshot
{
    public ProcessMonitorType ProcessMonitorType { get; set; } = ProcessMonitorType.ETW;

    public bool AutoStartInteractiveMonitor { get; set; }

    public bool LaunchOnSystemStartup { get; set; }
}
