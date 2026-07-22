using GameHelper.Core.Abstractions;
using GameHelper.Core.Models;

namespace GameHelper.Core.Services;

public sealed class SettingsService : ISettingsService
{
    private readonly IGameConfiguration _gameConfiguration;

    public SettingsService(IGameConfiguration gameConfiguration)
    {
        _gameConfiguration = gameConfiguration;
    }

    public AppSettingsSnapshot Get()
    {
        var config = _gameConfiguration.Read();
        return ToSnapshot(config);
    }

    public AppSettingsSnapshot Update(UpdateAppSettingsRequest request)
    {
        var config = _gameConfiguration.Change(current =>
        {
            current.ProcessMonitorType = request.ProcessMonitorType ?? current.ProcessMonitorType;
            current.AutoStartInteractiveMonitor = request.AutoStartInteractiveMonitor;
            current.LaunchOnSystemStartup = request.LaunchOnSystemStartup;
        });
        return ToSnapshot(config);
    }

    private static AppSettingsSnapshot ToSnapshot(AppConfig config) => new()
    {
        ProcessMonitorType = config.ProcessMonitorType,
        AutoStartInteractiveMonitor = config.AutoStartInteractiveMonitor,
        LaunchOnSystemStartup = config.LaunchOnSystemStartup
    };
}
