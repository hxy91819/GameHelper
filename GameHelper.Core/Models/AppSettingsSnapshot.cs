namespace GameHelper.Core.Models;

public sealed class AppSettingsSnapshot
{
    public ProcessMonitorType ProcessMonitorType { get; set; } = ProcessMonitorType.ETW;


    public bool LaunchOnSystemStartup { get; set; }
    public HdrSettings? Hdr { get; set; }
    public BreakReminderSettings? BreakReminder { get; set; }
    public MonitorModeSettings? MonitorMode { get; set; }
    public CheatEngineSettings? CheatEngine { get; set; }
    public SyncSettings? SyncSettings { get; set; }
    public ReportSettings? Report { get; set; }
    public IReadOnlyList<GameHelper.Core.Abstractions.HdrDisplayInfo> Displays { get; set; } = [];
    public bool DisplaysAvailable { get; set; }
    public IReadOnlyList<string> RestartRequired { get; set; } = [];
    public string? ApplyWarning { get; set; }
}
