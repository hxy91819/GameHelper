namespace GameHelper.Core.Models;

public sealed class UpdateAppSettingsRequest
{
    public ProcessMonitorType? ProcessMonitorType { get; set; }


    public bool LaunchOnSystemStartup { get; set; }
    /// <summary>Open the library in full-screen console mode by default.</summary>
    public bool ConsoleModeOnLibraryOpen { get; set; }
    public HdrSettings? Hdr { get; set; }
    public BreakReminderSettings? BreakReminder { get; set; }
    public MonitorModeSettings? MonitorMode { get; set; }
    public CheatEngineSettings? CheatEngine { get; set; }
    public SyncSettings? SyncSettings { get; set; }
    public ReportSettings? Report { get; set; }
}
