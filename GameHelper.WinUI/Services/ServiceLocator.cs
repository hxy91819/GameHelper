using GameHelper.Core.Abstractions;
using GameHelper.Core.Models;
using GameHelper.Core.Services;
using GameHelper.Infrastructure.Controllers;
using GameHelper.Infrastructure.Processes;
using GameHelper.Infrastructure.Providers;
using GameHelper.Infrastructure.Resolvers;
using GameHelper.Infrastructure.Startup;
using GameHelper.WinUI.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Linq;

namespace GameHelper.WinUI.Services;

public static class ServiceLocator
{
    private static IServiceProvider? _provider;

    public static void Initialize()
    {
        if (_provider is not null)
        {
            return;
        }

        var host = Host.CreateDefaultBuilder()
            .ConfigureLogging(logging =>
            {
                logging.ClearProviders();
                logging.AddConsole();
                logging.AddProvider(new UiLoggerProvider(UiLogSink.Instance));
            })
            .ConfigureServices(services =>
            {
                services.AddSingleton(UiLogSink.Instance);
                services.AddSingleton<IConfigProvider>(_ => new YamlConfigProvider());
                services.AddSingleton<IAppConfigProvider>(sp => (YamlConfigProvider)sp.GetRequiredService<IConfigProvider>());
                services.AddSingleton<IProcessMonitor>(sp =>
                {
                    var appConfigProvider = sp.GetRequiredService<IAppConfigProvider>();
                    var logger = sp.GetRequiredService<ILoggerFactory>().CreateLogger("WinUI.ProcessMonitor");
                    var appConfig = appConfigProvider.LoadAppConfig();
                    var preferredMonitor = appConfig.ProcessMonitorType ?? ProcessMonitorType.ETW;
                    var allowedProcesses = appConfig.Games?
                        .Select(game => game.ExecutableName ?? game.Name ?? game.DataKey)
                        .Where(name => !string.IsNullOrWhiteSpace(name))
                        .ToArray();

                    return ProcessMonitorFactory.CreateWithFallback(preferredMonitor, allowedProcesses, logger);
                });
                services.AddSingleton<IHdrController, WindowsHdrController>();
                services.AddSingleton<IPlayTimeService, CsvBackedPlayTimeService>();
                services.AddSingleton<IAutoStartManager>(sp =>
                    new WindowsAutoStartManager(sp.GetRequiredService<ILogger<WindowsAutoStartManager>>()));
                services.AddSingleton<ISteamGameResolver, SteamGameResolver>();
                services.AddSingleton<IGameAutomationService, GameAutomationService>();
                services.AddSingleton<IMonitorControlService, MonitorControlService>();
                services.AddSingleton<ISettingsService, SettingsService>();
                services.AddSingleton<IGameCatalogService, GameCatalogService>();
                services.AddSingleton<IPlaytimeSnapshotProvider, FilePlaytimeSnapshotProvider>();
                services.AddSingleton<IStatisticsService, StatisticsService>();

                services.AddSingleton<ShellViewModel>();
                services.AddSingleton<SettingsViewModel>();
                services.AddSingleton<GamesViewModel>();
                services.AddSingleton<StatsViewModel>();
            })
            .Build();

        _provider = host.Services;
    }

    public static T GetRequiredService<T>() where T : notnull
    {
        if (_provider is null)
        {
            throw new InvalidOperationException("ServiceLocator.Initialize must be called before resolving services.");
        }

        return _provider.GetRequiredService<T>();
    }
}
