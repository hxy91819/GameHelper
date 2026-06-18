using System;
using GameHelper.ConsoleHost.Interactive;
using GameHelper.ConsoleHost.Services;
using GameHelper.ConsoleHost.Utilities;
using GameHelper.Core.Abstractions;
using GameHelper.Core.Services;
using GameHelper.Infrastructure.Controllers;
using GameHelper.Infrastructure.Processes;
using GameHelper.Infrastructure.Providers;
using GameHelper.Infrastructure.Resolvers;
using GameHelper.Infrastructure.Startup;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace GameHelper.ConsoleHost;

public static class ConsoleHostBootstrapper
{
    public static IHostBuilder CreateBuilder(string[] args, ParsedArguments parsedArgs)
    {
        ArgumentNullException.ThrowIfNull(parsedArgs);

        return Host.CreateDefaultBuilder(args)
            .ConfigureLogging(logging =>
            {
                logging.ClearProviders();
                logging.AddConsole();
                logging.SetMinimumLevel(parsedArgs.EnableDebug ? LogLevel.Debug : LogLevel.Information);
            })
            .ConfigureServices(services => ConfigureServices(services, parsedArgs));
    }

    public static void ConfigureServices(IServiceCollection services, ParsedArguments parsedArgs)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(parsedArgs);

        services.AddSingleton<IConfigProvider>(_ =>
        {
            if (!string.IsNullOrWhiteSpace(parsedArgs.ConfigOverride))
            {
                return new YamlConfigProvider(parsedArgs.ConfigOverride!);
            }

            return new YamlConfigProvider();
        });
        services.AddSingleton<IAppConfigProvider>(sp => (YamlConfigProvider)sp.GetRequiredService<IConfigProvider>());
        services.AddSingleton<IProcessMonitor>(sp =>
        {
            var logger = sp.GetRequiredService<ILoggerFactory>().CreateLogger("GameHelper.ConsoleHost.ProcessMonitor");
            var monitorMode = parsedArgs.MonitorDryRun
                ? MonitorModeSelection.Resolve(parsedArgs, null)
                : MonitorModeSelection.Resolve(
                    parsedArgs,
                    sp.GetRequiredService<IAppConfigProvider>().LoadAppConfig().ProcessMonitorType);

            if (monitorMode.IsDryRun)
            {
                logger.LogInformation("Monitor dry-run enabled; using no-op process monitor.");
                return ProcessMonitorFactory.CreateNoOp();
            }

            switch (monitorMode.Source)
            {
                case MonitorModeSource.CommandLine:
                    logger.LogInformation("Using monitor type from command line: {MonitorType}", monitorMode.MonitorType);
                    break;
                case MonitorModeSource.Config:
                    logger.LogInformation("Using monitor type from config: {MonitorType}", monitorMode.MonitorType);
                    break;
                case MonitorModeSource.InvalidCommandLine:
                    logger.LogWarning("Invalid monitor type '{MonitorType}' specified in command line, using default ETW", monitorMode.RequestedMonitorType);
                    break;
                default:
                    logger.LogInformation("Using default monitor type: {MonitorType}", monitorMode.MonitorType);
                    break;
            }

            try
            {
                return ProcessMonitorFactory.CreateWithFallback(monitorMode.MonitorType, null, logger);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to create process monitor, falling back to no-op");
                return ProcessMonitorFactory.CreateNoOp();
            }
        });

        if (OperatingSystem.IsWindows())
        {
            services.AddSingleton<IHdrController, WindowsHdrController>();
            services.AddSingleton<IProcessPathResolver, WindowsProcessPathResolver>();
        }
        else
        {
            services.AddSingleton<IHdrController, NoOpHdrController>();
            services.AddSingleton<IProcessPathResolver, NoOpProcessPathResolver>();
        }

        services.AddSingleton<IPlayTimeService, CsvBackedPlayTimeService>();
        services.AddSingleton<IAutoStartManager>(sp =>
        {
            if (OperatingSystem.IsWindows())
            {
                return new WindowsAutoStartManager(sp.GetRequiredService<ILogger<WindowsAutoStartManager>>());
            }

            return new NoOpAutoStartManager();
        });
        services.AddSingleton<IGameAutomationService, GameAutomationService>();
        services.AddSingleton<IMonitorControlService, MonitorControlService>();
        services.AddSingleton<ISettingsService, SettingsService>();
        services.AddSingleton<IGameCatalogService, GameCatalogService>();
        services.AddSingleton<IPlaytimeSnapshotProvider, FilePlaytimeSnapshotProvider>();
        services.AddSingleton<IStatisticsService, StatisticsService>();
        services.AddSingleton<ISteamGameResolver, SteamGameResolver>();

        services.AddSingleton<IFileDropProcessor, DefaultFileDropProcessor>();
        services.AddSingleton<IFileDropRequestHandler, FileDropRequestHandler>();
        services.AddSingleton<FileDropIpcServer>();
        services.AddSingleton<IFileDropIpcServer>(sp => sp.GetRequiredService<FileDropIpcServer>());
        services.AddSingleton<IHostedService>(sp => sp.GetRequiredService<FileDropIpcServer>());

        services.AddHostedService<Worker>();
    }
}
