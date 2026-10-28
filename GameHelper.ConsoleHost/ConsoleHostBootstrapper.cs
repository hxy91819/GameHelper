using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using GameHelper.ConsoleHost.Services;
using GameHelper.ConsoleHost.Utilities;
using GameHelper.Core.Abstractions;
using GameHelper.Core.Services;
using GameHelper.Core.Utilities;
using GameHelper.Infrastructure.Controllers;
using GameHelper.Infrastructure.Notifications;
using GameHelper.Infrastructure.Processes;
using GameHelper.Infrastructure.Power;
using GameHelper.Infrastructure.Providers;
using GameHelper.Infrastructure.Resolvers;
using GameHelper.Infrastructure.Startup;
using GameHelper.Infrastructure.Storage.Sqlite;
using GameHelper.Infrastructure.Upload;
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

        services.AddSingleton<IGameConfiguration>(_ =>
        {
            if (!string.IsNullOrWhiteSpace(parsedArgs.ConfigOverride))
            {
                return new YamlConfigProvider(parsedArgs.ConfigOverride!);
            }

            return new YamlConfigProvider();
        });
        services.AddSingleton<IProcessMonitor>(sp =>
        {
            var loggerFactory = sp.GetRequiredService<ILoggerFactory>();
            var logger = loggerFactory.CreateLogger("GameHelper.ConsoleHost.ProcessMonitor");
            var monitorMode = parsedArgs.MonitorDryRun
                ? MonitorModeSelection.Resolve(parsedArgs, null)
                : MonitorModeSelection.Resolve(
                    parsedArgs,
                    sp.GetRequiredService<IGameConfiguration>().Read().ProcessMonitorType);

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
                return ProcessMonitorFactory.CreateWithFallback(
                    monitorMode.MonitorType, null, loggerFactory,
                    sp.GetRequiredService<IProcessPathResolver>());
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
            services.AddSingleton<IMonitorModeController, DdcMonitorModeController>();
            services.AddSingleton<IProcessIdentityProbe, WindowsProcessIdentityProbe>();
            services.AddSingleton<IUserLocationSource, WindowsLocationProvider>();
            services.AddSingleton<ILocationCacheStore>(sp => new JsonLocationCacheStore(
                AppDataPath.GetLocationCachePath(),
                sp.GetRequiredService<ILoggerFactory>().CreateLogger<JsonLocationCacheStore>(),
                sp.GetRequiredService<TimeProvider>()));
            services.AddSingleton<ISystemPowerEvents, WindowsSystemPowerEvents>();
        }
        else
        {
            services.AddSingleton<IHdrController, NoOpHdrController>();
            services.AddSingleton<IProcessPathResolver, NoOpProcessPathResolver>();
            services.AddSingleton<IMonitorModeController, NoOpMonitorModeController>();
        }

        services.AddSingleton<SqliteConnectionFactory>();
        services.AddSingleton<ISessionRatingNotifier>(sp => new SessionRatingNotifier(id =>
        {
            if (sp.GetService<ISystemTrayService>() is WindowsSystemTrayService tray) tray.Invite(id);
        }, sp.GetRequiredService<ILogger<SessionRatingNotifier>>()));
        services.AddSingleton<SessionStorage>();
        services.AddSingleton<SessionRatings>(sp => new SessionRatings(new SqliteSessionRatingStore(
            sp.GetRequiredService<SqliteConnectionFactory>(), sp.GetRequiredService<TimeProvider>()), sp.GetRequiredService<TimeProvider>()));
        services.AddSingleton<IPlayTimeService>(sp => sp.GetRequiredService<SessionStorage>().PlayTime);
        services.AddSingleton<IInFlightSessionStore>(sp => sp.GetRequiredService<SessionStorage>().InFlight);
        services.AddSingleton<IGameSpeedController, CheatEngineSpeedController>();
        services.AddSingleton<IForegroundActivityProvider, WindowsForegroundActivityProvider>();
        services.AddSingleton<IBreakReminderNotifier, WindowsBreakReminderNotifier>();
        services.AddSingleton<IBreakReminderService, BreakReminderService>();
        services.AddSingleton<IAutoStartManager>(sp =>
        {
            if (OperatingSystem.IsWindows() && string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("GAMEHELPER_DATA_DIR")))
            {
                return new WindowsAutoStartManager(sp.GetRequiredService<ILogger<WindowsAutoStartManager>>());
            }

            return new NoOpAutoStartManager();
        });
        services.AddSingleton<IGameAutomationService, GameAutomationService>();
        services.AddSingleton<IGameLauncher, WindowsGameLauncher>();
        services.AddSingleton<IGameLaunchCoordinator>(sp => (IGameLaunchCoordinator)sp.GetRequiredService<IGameAutomationService>());
        services.AddSingleton<IGameLibrary, GameLibrary>();
        services.AddSingleton<GameLibraryServer>();
        services.AddSingleton<IHostedService>(sp => sp.GetRequiredService<GameLibraryServer>());
        services.AddSingleton<IConfigWatcher>(sp =>
        {
            var configPath = ((IConfigPathProvider)sp.GetRequiredService<IGameConfiguration>()).ConfigPath;
            var logger = sp.GetRequiredService<ILoggerFactory>().CreateLogger("GameHelper.ConfigFileWatcher");
            return new ConfigFileWatcher(
                configPath,
                () => sp.GetRequiredService<IGameAutomationService>().ReloadConfig(),
                logger);
        });
        services.AddSingleton<IMonitorControlService, MonitorControlService>();
        services.AddSingleton<ISettingsService, SettingsService>();
        services.AddSingleton<IGameCatalogService, GameCatalogService>();
        services.AddSingleton<IPlaytimeSnapshotProvider, CompatibilityPlaytimeSnapshotProvider>();
        services.AddSingleton<IStatisticsService, StatisticsService>();
        services.AddSingleton<ISteamGameResolver, SteamGameResolver>();
        services.AddSingleton<ILocalGameDiscovery, LocalGameDiscovery>();

        services.AddSingleton<TimeProvider>(TimeProvider.System);
        services.AddSingleton<StatsReportBuilder>();
        services.AddSingleton<GitHubUploadChannelProvider>();
        services.AddSingleton<IStatsUploadChannelProvider>(sp => sp.GetRequiredService<GitHubUploadChannelProvider>());
        services.AddSingleton<ISyncService>(sp => new SyncService(
            sp.GetRequiredService<IGameConfiguration>(),
            new CompatibilityPlaytimeSnapshotProvider(sp.GetRequiredService<SessionStorage>()),
            sp.GetRequiredService<IStatsUploadChannelProvider>(), sp.GetRequiredService<StatsReportBuilder>(),
            sp.GetRequiredService<TimeProvider>(), sp.GetRequiredService<ILogger<SyncService>>()));
        services.AddSingleton<IHostedService, SyncBackgroundService>();
        services.AddHostedService<StorageMaintenanceBackgroundService>();

        services.AddSingleton<IConfigPathProvider>(sp => (IConfigPathProvider)sp.GetRequiredService<IGameConfiguration>());
        services.AddSingleton<FileDropIntake>();
        services.AddSingleton<FileDropIpcServer>();
        services.AddSingleton<IFileDropIpcServer>(sp => sp.GetRequiredService<FileDropIpcServer>());
        services.AddSingleton<IHostedService>(sp => sp.GetRequiredService<FileDropIpcServer>());

        if (OperatingSystem.IsWindows() && SystemTrayLogic.ShouldStart(parsedArgs.Background,
            Environment.GetEnvironmentVariable("GAMEHELPER_DATA_DIR")))
        {
            services.AddSingleton<ISystemTrayService>(sp => new WindowsSystemTrayService(
                () =>
                {
                    var monitor = sp.GetRequiredService<IMonitorControlService>();
                    var sessions = sp.GetRequiredService<IGameLaunchCoordinator>().GetRunningSessionStarts();
                    var names = sp.GetRequiredService<IGameConfiguration>().Read().Games
                        .ToDictionary(game => game.DataKey, game => string.IsNullOrWhiteSpace(game.DisplayName) ? game.DataKey : game.DisplayName);
                    return SystemTrayLogic.Summary(monitor.IsRunning, sessions, names,
                        sp.GetRequiredService<TimeProvider>().GetLocalNow().DateTime);
                },
                new TrayMenuService(sp.GetRequiredService<IGameLibrary>(),
                    new TrayHdrControl(sp.GetRequiredService<IHdrController>()),
                    sp.GetRequiredService<IBreakReminderService>(), sp.GetRequiredService<TimeProvider>(),
                    () =>
                    {
                        var config = sp.GetRequiredService<IConfigPathProvider>().ConfigPath;
                        using var process = Process.Start(SystemTrayLogic.LibraryStartInfo(AppContext.BaseDirectory, config, File.Exists));
                    },
                    () => sp.GetRequiredService<IHostApplicationLifetime>().StopApplication()),
                sp.GetRequiredService<ILogger<WindowsSystemTrayService>>(),
                () => sp.GetRequiredService<SessionStorage>().Warning,
                id =>
                {
                    var start = SystemTrayLogic.LibraryStartInfo(AppContext.BaseDirectory, sp.GetRequiredService<IConfigPathProvider>().ConfigPath, File.Exists);
                    start.ArgumentList.Add("--rate-session"); start.ArgumentList.Add(id.ToString("D"));
                    using var process = Process.Start(start);
                }));
        }

        services.AddHostedService<Worker>();
    }
}
