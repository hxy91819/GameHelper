using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GameHelper.Core.Abstractions;
using GameHelper.Core.Models;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Dispatching;

namespace GameHelper.WinUI.ViewModels;

public partial class ShellViewModel : ObservableObject
{
    private readonly IMonitorControlService _monitorControlService;
    private readonly IProcessMonitor _processMonitor;
    private readonly ILogger<ShellViewModel> _logger;
    private readonly DispatcherQueue _dispatcher;
    private bool _isMonitorRunning;

    public ShellViewModel(
        IMonitorControlService monitorControlService,
        IProcessMonitor processMonitor,
        ILogger<ShellViewModel> logger)
    {
        _monitorControlService = monitorControlService;
        _processMonitor = processMonitor;
        _logger = logger;
        _dispatcher = DispatcherQueue.GetForCurrentThread();
        _isMonitorRunning = _monitorControlService.IsRunning;

        _processMonitor.ProcessStarted += OnProcessStarted;
        _processMonitor.ProcessStopped += OnProcessStopped;
        AppendLog("Monitor ready.");
    }

    public string MonitorButtonText => _monitorControlService.IsRunning ? "Stop Monitor" : "Start Monitor";

    public bool IsMonitorRunning
    {
        get => _isMonitorRunning;
        private set => SetProperty(ref _isMonitorRunning, value);
    }

    public string MonitorStatusText => IsMonitorRunning ? "Running" : "Stopped";

    public ObservableCollection<MonitorLogEntry> MonitorLogs { get; } = new();

    [RelayCommand]
    private void ToggleMonitor()
    {
        try
        {
            if (_monitorControlService.IsRunning)
            {
                _monitorControlService.Stop();
                IsMonitorRunning = false;
                AppendLog("Monitor stopped.");
            }
            else
            {
                _monitorControlService.Start();
                IsMonitorRunning = true;
                AppendLog("Monitor started.");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to toggle monitor");
            AppendLog($"Monitor error: {ex.Message}", "ERROR");
        }

        OnPropertyChanged(nameof(MonitorButtonText));
        OnPropertyChanged(nameof(MonitorStatusText));
    }

    private void OnProcessStarted(ProcessEventInfo info)
    {
        var detail = string.IsNullOrWhiteSpace(info.ExecutablePath)
            ? info.ExecutableName
            : $"{info.ExecutableName} ({info.ExecutablePath})";
        AppendLog($"Process started: {detail}");
    }

    private void OnProcessStopped(ProcessEventInfo info)
    {
        var detail = string.IsNullOrWhiteSpace(info.ExecutablePath)
            ? info.ExecutableName
            : $"{info.ExecutableName} ({info.ExecutablePath})";
        AppendLog($"Process stopped: {detail}");
    }

    private void AppendLog(string message, string level = "INFO")
    {
        var entry = new MonitorLogEntry(DateTimeOffset.Now, level, message);
        if (_dispatcher.HasThreadAccess)
        {
            AddLogEntry(entry);
            return;
        }

        _dispatcher.TryEnqueue(() => AddLogEntry(entry));
    }

    private void AddLogEntry(MonitorLogEntry entry)
    {
        MonitorLogs.Insert(0, entry);

        const int maxEntries = 500;
        if (MonitorLogs.Count > maxEntries)
        {
            MonitorLogs.RemoveAt(MonitorLogs.Count - 1);
        }
    }

    [RelayCommand]
    private void ClearLogs()
    {
        MonitorLogs.Clear();
        AppendLog("Logs cleared.");
    }
}
