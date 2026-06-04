using GameHelper.Core.Abstractions;
using GameHelper.Core.Models;

namespace GameHelper.Core.Services;

public sealed class SettingsService : ISettingsService
{
    private readonly IAppConfigProvider _appConfigProvider;

    public SettingsService(IAppConfigProvider appConfigProvider)
    {
        _appConfigProvider = appConfigProvider;
    }

    public AppSettingsSnapshot Get()
    {
        var config = _appConfigProvider.LoadAppConfig();
        return ToSnapshot(config);
    }

    public AppSettingsSnapshot Update(UpdateAppSettingsRequest request)
    {
        var config = _appConfigProvider.LoadAppConfig();
        config.ProcessMonitorType = request.ProcessMonitorType ?? config.ProcessMonitorType ?? ProcessMonitorType.ETW;
        config.AutoStartInteractiveMonitor = request.AutoStartInteractiveMonitor;
        config.LaunchOnSystemStartup = request.LaunchOnSystemStartup;
        _appConfigProvider.SaveAppConfig(config);
        return ToSnapshot(config);
    }

    private static AppSettingsSnapshot ToSnapshot(AppConfig config) => new()
    {
        ProcessMonitorType = config.ProcessMonitorType ?? ProcessMonitorType.ETW,
        AutoStartInteractiveMonitor = config.AutoStartInteractiveMonitor,
        LaunchOnSystemStartup = config.LaunchOnSystemStartup
    };
}
