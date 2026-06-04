using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GameHelper.Core.Abstractions;
using GameHelper.Core.Models;

namespace GameHelper.WinUI.ViewModels;

public partial class SettingsViewModel : ObservableObject
{
    private readonly ISettingsService _settingsService;

    [ObservableProperty]
    private string selectedMonitorType = ProcessMonitorType.ETW.ToString();

    [ObservableProperty]
    private bool autoStartInteractiveMonitor;

    [ObservableProperty]
    private bool launchOnSystemStartup;

    public SettingsViewModel(ISettingsService settingsService)
    {
        _settingsService = settingsService;
        AvailableMonitorTypes = Enum.GetNames<ProcessMonitorType>();
        Load();
    }

    public IReadOnlyList<string> AvailableMonitorTypes { get; }

    [RelayCommand]
    private void Save()
    {
        ProcessMonitorType? monitorType = null;
        if (Enum.TryParse<ProcessMonitorType>(SelectedMonitorType, true, out var parsed))
        {
            monitorType = parsed;
        }

        var snapshot = _settingsService.Update(new UpdateAppSettingsRequest
        {
            ProcessMonitorType = monitorType,
            AutoStartInteractiveMonitor = AutoStartInteractiveMonitor,
            LaunchOnSystemStartup = LaunchOnSystemStartup
        });

        SelectedMonitorType = snapshot.ProcessMonitorType.ToString();
        AutoStartInteractiveMonitor = snapshot.AutoStartInteractiveMonitor;
        LaunchOnSystemStartup = snapshot.LaunchOnSystemStartup;
    }

    private void Load()
    {
        var snapshot = _settingsService.Get();
        SelectedMonitorType = snapshot.ProcessMonitorType.ToString();
        AutoStartInteractiveMonitor = snapshot.AutoStartInteractiveMonitor;
        LaunchOnSystemStartup = snapshot.LaunchOnSystemStartup;
    }
}
