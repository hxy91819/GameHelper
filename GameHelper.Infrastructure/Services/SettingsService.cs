using System.Text.Json;
using GameHelper.Core.Abstractions;
using GameHelper.Core.Models;
using GameHelper.Infrastructure.Validators;

namespace GameHelper.Core.Services;

/// <summary>Validates global settings before committing, then applies runtime integrations.</summary>
public sealed class SettingsService(IGameConfiguration gameConfiguration,
    IAutoStartManager? autoStart = null, IGameAutomationService? automation = null, IHdrController? hdr = null) : ISettingsService
{
    private readonly object _gate = new();
    private readonly AppConfig _startup = gameConfiguration.Read();

    public AppSettingsSnapshot Get() => ToSnapshot(gameConfiguration.Read());

    public AppSettingsSnapshot Update(UpdateAppSettingsRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        lock (_gate)
        {
            var config = gameConfiguration.Change(current =>
            {
                current.ProcessMonitorType = request.ProcessMonitorType ?? current.ProcessMonitorType;
                current.LaunchOnSystemStartup = request.LaunchOnSystemStartup;
                if (request.Hdr is not null) current.Hdr = Clone(request.Hdr);
                if (request.BreakReminder is not null) current.BreakReminder = Clone(request.BreakReminder);
                if (request.MonitorMode is not null) current.MonitorMode = Clone(request.MonitorMode);
                if (request.CheatEngine is not null) current.CheatEngine = Clone(request.CheatEngine);
                if (request.SyncSettings is not null) current.SyncSettings = Clone(request.SyncSettings);
                var validation = YamlConfigValidator.Validate(current);
                if (!validation.IsValid) throw new ArgumentException(string.Join("\n", validation.Errors));
            });
            // Persistence succeeded: report partial application explicitly so the client cannot mistake it for an unsaved draft.
            var warnings = new List<string>();
            try { autoStart?.SetEnabled(config.LaunchOnSystemStartup); }
            catch (Exception ex) { warnings.Add("开机启动未应用：" + ex.Message); }
            try { automation?.ReloadConfig(); }
            catch (Exception ex) { warnings.Add("运行时配置未重载：" + ex.Message); }
            var result = ToSnapshot(config);
            result.ApplyWarning = warnings.Count == 0 ? null : string.Join("；", warnings);
            return result;
        }
    }

    private AppSettingsSnapshot ToSnapshot(AppConfig config)
    {
        IReadOnlyList<HdrDisplayInfo> displays = [];
        var available = hdr is not null && hdr.TryGetDisplays(out displays);
        var restart = new List<string>();
        if (config.ProcessMonitorType != _startup.ProcessMonitorType) restart.Add(nameof(config.ProcessMonitorType));
        return new AppSettingsSnapshot
        {
            ProcessMonitorType = config.ProcessMonitorType,
            LaunchOnSystemStartup = config.LaunchOnSystemStartup,
            Hdr = Clone(config.Hdr), BreakReminder = Clone(config.BreakReminder),
            MonitorMode = Clone(config.MonitorMode), CheatEngine = Clone(config.CheatEngine),
            SyncSettings = Clone(config.SyncSettings), DisplaysAvailable = available,
            Displays = displays, RestartRequired = restart
        };
    }

    private static T? Clone<T>(T? value) => value is null ? default : JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(value));
}
