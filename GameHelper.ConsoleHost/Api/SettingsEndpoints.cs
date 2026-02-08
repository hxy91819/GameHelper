using GameHelper.ConsoleHost.Api.Models;
using GameHelper.Core.Abstractions;
using GameHelper.Core.Models;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace GameHelper.ConsoleHost.Api;

public static class SettingsEndpoints
{
    public static IEndpointRouteBuilder MapSettingsEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/settings");

        group.MapGet("/", (IAppConfigProvider appConfigProvider) =>
        {
            var config = appConfigProvider.LoadAppConfig();
            return Results.Ok(ToDto(config));
        });

        group.MapPut("/", (UpdateSettingsRequest request, IAppConfigProvider appConfigProvider) =>
        {
            var config = appConfigProvider.LoadAppConfig();

            if (!string.IsNullOrWhiteSpace(request.ProcessMonitorType) &&
                Enum.TryParse<ProcessMonitorType>(request.ProcessMonitorType, true, out var monitorType))
            {
                config.ProcessMonitorType = monitorType;
            }

            config.AutoStartInteractiveMonitor = request.AutoStartInteractiveMonitor;
            config.LaunchOnSystemStartup = request.LaunchOnSystemStartup;

            appConfigProvider.SaveAppConfig(config);
            return Results.Ok(ToDto(config));
        });

        return app;
    }

    private static SettingsDto ToDto(AppConfig config) => new()
    {
        ProcessMonitorType = (config.ProcessMonitorType ?? ProcessMonitorType.ETW).ToString(),
        AutoStartInteractiveMonitor = config.AutoStartInteractiveMonitor,
        LaunchOnSystemStartup = config.LaunchOnSystemStartup
    };
}
