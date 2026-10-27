namespace GameHelper.Core.Models;

public sealed class UpdateAppSettingsRequest
{
    public ProcessMonitorType? ProcessMonitorType { get; set; }


    public bool LaunchOnSystemStartup { get; set; }
    public HdrSettings? Hdr { get; set; }
    public BreakReminderSettings? BreakReminder { get; set; }
    public MonitorModeSettings? MonitorMode { get; set; }
    public CheatEngineSettings? CheatEngine { get; set; }
    public SyncSettings? SyncSettings { get; set; }
}
