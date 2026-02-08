namespace GameHelper.ConsoleHost.Api.Models;

public sealed class SettingsDto
{
    public string ProcessMonitorType { get; set; } = "ETW";
    public bool AutoStartInteractiveMonitor { get; set; }
    public bool LaunchOnSystemStartup { get; set; }
}

public sealed class UpdateSettingsRequest
{
    public string? ProcessMonitorType { get; set; }
    public bool AutoStartInteractiveMonitor { get; set; }
    public bool LaunchOnSystemStartup { get; set; }
}
