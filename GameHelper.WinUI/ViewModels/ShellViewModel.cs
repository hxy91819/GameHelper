using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GameHelper.Core.Abstractions;

namespace GameHelper.WinUI.ViewModels;

public partial class ShellViewModel : ObservableObject
{
    private readonly IMonitorControlService _monitorControlService;

    public ShellViewModel(IMonitorControlService monitorControlService)
    {
        _monitorControlService = monitorControlService;
    }

    public string MonitorButtonText => _monitorControlService.IsRunning ? "Stop Monitor" : "Start Monitor";

    [RelayCommand]
    private void ToggleMonitor()
    {
        if (_monitorControlService.IsRunning)
        {
            _monitorControlService.Stop();
        }
        else
        {
            _monitorControlService.Start();
        }

        OnPropertyChanged(nameof(MonitorButtonText));
    }
}
