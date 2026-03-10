namespace GameHelper.Core.Models;

public sealed class UpdateAppSettingsRequest
{
    public ProcessMonitorType? ProcessMonitorType { get; set; }

    public bool AutoStartInteractiveMonitor { get; set; }

    public bool LaunchOnSystemStartup { get; set; }

    public double DefaultSpeedMultiplier { get; set; }

    public string SpeedToggleHotkey { get; set; } = string.Empty;
}
