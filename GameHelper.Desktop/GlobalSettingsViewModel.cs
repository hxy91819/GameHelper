using GameHelper.Core.Abstractions;
using GameHelper.Core.Models;

namespace GameHelper.Desktop;

/// <summary>Detached settings draft; offline display rules and fields outside the form survive editing.</summary>
public sealed class GlobalSettingsViewModel
{
    private readonly AppSettingsSnapshot _snapshot;
    public GlobalSettingsViewModel(AppSettingsSnapshot snapshot)
    {
        _snapshot = snapshot;
        ProcessMonitorType = snapshot.ProcessMonitorType;
        LaunchOnSystemStartup = snapshot.LaunchOnSystemStartup;
        var reminder = snapshot.BreakReminder ?? new();
        ReminderEnabled = reminder.Enabled; PlayMinutes = reminder.PlayMinutes; BreakMinutes = reminder.BreakMinutes;
        RepeatMinutes = reminder.RepeatMinutes; Sound = reminder.Sound;
        MonitorMode = snapshot.MonitorMode ?? new();
        CheatEnginePath = snapshot.CheatEngine?.Executable ?? string.Empty;
        DefaultSpeedHotkeys = SpeedHotkey.FormatLines(snapshot.CheatEngine?.SpeedHotkeys);
        Sync = snapshot.SyncSettings ?? new();
        Sync.Method = Sync.NormalizedMethod;
        var policy = snapshot.Hdr ?? new();
        Displays = snapshot.Displays.Select(display => new HdrDisplayChoice(display,
            policy.Allows(display.Name, display.DevicePath))).ToList();
    }
    public ProcessMonitorType ProcessMonitorType { get; set; }
    public bool LaunchOnSystemStartup { get; set; }
    public bool ReminderEnabled { get; set; }
    public int PlayMinutes { get; set; }
    public int BreakMinutes { get; set; }
    public int RepeatMinutes { get; set; }
    public bool Sound { get; set; }
    public MonitorModeSettings MonitorMode { get; }
    public string CheatEnginePath { get; set; }
    public string DefaultSpeedHotkeys { get; set; }
    public SyncSettings Sync { get; }
    public IReadOnlyList<HdrDisplayChoice> Displays { get; }
    public string DisplayStatus => _snapshot.DisplaysAvailable ? (Displays.Count == 0 ? "当前没有活跃显示器。" : "勾选允许自动开启 HDR 的显示器；未连接显示器的规则会保留。") : "暂时无法读取显示器；已保存的规则保持不变。";
    public string RestartMessage => _snapshot.RestartRequired.Count == 0 ? "" : "监控方式已修改，重启后台后生效。";

    public UpdateAppSettingsRequest ToRequest()
    {
        var policy = _snapshot.Hdr ?? new();
        var allowed = policy.AllowedDisplays.ToList();
        var disabled = policy.DisabledDisplays.ToList();
        var whitelist = allowed.Count > 0;
        foreach (var choice in Displays)
        {
            bool Matches(string value) => value.Equals(choice.Display.Name, StringComparison.OrdinalIgnoreCase) || value.Equals(choice.Display.DevicePath, StringComparison.OrdinalIgnoreCase);
            allowed.RemoveAll(value => Matches(value)); disabled.RemoveAll(value => Matches(value));
            var key = string.IsNullOrEmpty(choice.Display.DevicePath) ? choice.Display.Name : choice.Display.DevicePath;
            if (!choice.Selected) disabled.Add(key);
            else if (whitelist) allowed.Add(key);
        }
        return new UpdateAppSettingsRequest
        {
            ProcessMonitorType = ProcessMonitorType, LaunchOnSystemStartup = LaunchOnSystemStartup,
            Hdr = new HdrSettings { AllowedDisplays = allowed, DisabledDisplays = disabled },
            BreakReminder = new BreakReminderSettings { Enabled = ReminderEnabled, PlayMinutes = PlayMinutes,
                BreakMinutes = BreakMinutes, RepeatMinutes = RepeatMinutes, Sound = Sound },
            MonitorMode = MonitorMode, CheatEngine = new CheatEngineSettings
            {
                Executable = CheatEnginePath.Trim(), SpeedHotkeys = SpeedHotkey.ParseLines(DefaultSpeedHotkeys)
            }, SyncSettings = Sync
        };
    }
}

public sealed class HdrDisplayChoice(HdrDisplayInfo display, bool selected)
{
    public HdrDisplayInfo Display { get; } = display;
    public bool Selected { get; set; } = selected;
    public string Label => $"{Display.Name} · {(Display.Enabled ? "HDR 已开启" : Display.Supported ? "HDR 可用" : "不支持 HDR")}";
}
