using System;
using System.Linq;
using System.Threading;
using GameHelper.ConsoleHost;
using GameHelper.ConsoleHost.Commands;
using GameHelper.ConsoleHost.Interactive;
using GameHelper.ConsoleHost.Services;
using GameHelper.ConsoleHost.Utilities;
using GameHelper.Core.Abstractions;
using GameHelper.Core.Models;
using GameHelper.Core.Services;
using GameHelper.Infrastructure.Controllers;
using GameHelper.Infrastructure.Processes;
using GameHelper.Infrastructure.Providers;
using GameHelper.Infrastructure.Resolvers;
using GameHelper.Infrastructure.Startup;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

ConsoleEncoding.EnsureUtf8();

// Parse command line arguments
var parsedArgs = ArgumentParser.Parse(args);
var isFileDropRequest = FileDropHandler.LooksLikeFilePaths(parsedArgs.EffectiveArgs);
var claimedSingleInstance = ProcessInstanceGuard.TryClaim();
var startupMode = StartupModeResolver.Resolve(isFileDropRequest, claimedSingleInstance);

if (startupMode == StartupMode.ForwardFileDropToRunningInstance)
{
    try
    {
        var response = await FileDropIpcClient.SendAsync(parsedArgs.EffectiveArgs, parsedArgs.ConfigOverride).ConfigureAwait(false);
        var text = FormatDropResponse(response);
        FileDropHandler.TryShowMessageBox(text, "GameHelper");
        Environment.Exit(response.Success ? 0 : 1);
    }
    catch (Exception ex)
    {
        FileDropHandler.TryShowMessageBox($"转发到运行中实例失败: {ex.Message}", "GameHelper");
        Environment.Exit(1);
    }

    return;
}

if (startupMode == StartupMode.ExitAlreadyRunning)
{
    Console.WriteLine("检测到 GameHelper 已在运行，请勿重复启动。");
    return;
}

// Build host with dependency injection
var host = Host.CreateDefaultBuilder(args)
    .ConfigureLogging(logging =>
    {
        logging.ClearProviders();
        logging.AddConsole();
        logging.SetMinimumLevel(parsedArgs.EnableDebug ? LogLevel.Debug : LogLevel.Information);
    })
    .ConfigureServices(services =>
    {
        // Register core abstractions with infrastructure implementations
        // Config provider first, as the process monitor will read enabled game names to build a whitelist
        services.AddSingleton<IConfigProvider>(sp =>
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
            var logger = sp.GetRequiredService<ILogger<Program>>();
            if (parsedArgs.MonitorDryRun)
            {
                logger.LogInformation("Monitor dry-run enabled; using no-op process monitor.");
                return ProcessMonitorFactory.CreateNoOp();
            }

            var appConfigProvider = sp.GetRequiredService<IAppConfigProvider>();
            var appConfig = appConfigProvider.LoadAppConfig();

            // Determine monitor type: command line > config file > default (ETW)
            ProcessMonitorType monitorType = ProcessMonitorType.ETW; // default

            if (!string.IsNullOrWhiteSpace(parsedArgs.MonitorType))
            {
                if (Enum.TryParse<ProcessMonitorType>(parsedArgs.MonitorType, true, out var cmdLineType))
                {
                    monitorType = cmdLineType;
                    logger.LogInformation("Using monitor type from command line: {MonitorType}", monitorType);
                }
                else
                {
                    logger.LogWarning("Invalid monitor type '{MonitorType}' specified in command line, using default ETW", parsedArgs.MonitorType);
                }
            }
            else if (appConfig.ProcessMonitorType.HasValue)
            {
                monitorType = appConfig.ProcessMonitorType.Value;
                logger.LogInformation("Using monitor type from config: {MonitorType}", monitorType);
            }
            else
            {
                logger.LogInformation("Using default monitor type: {MonitorType}", monitorType);
            }

            // Get enabled games for whitelist (no filtering at monitor level - let GameAutomationService handle it)
            var configProvider = sp.GetRequiredService<IConfigProvider>();
            var gameConfigs = configProvider.Load();
            _ = gameConfigs
                .Where(kv => kv.Value?.IsEnabled == true)
                .Select(kv => kv.Key)
                .ToArray();

            // Create monitor with fallback support
            try
            {
                return ProcessMonitorFactory.CreateWithFallback(monitorType, null, logger);
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
        }
        else
        {
            services.AddSingleton<IHdrController, NoOpHdrController>();
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
        // Steam URL resolver for optional .url drag&drop support
        services.AddSingleton<ISteamGameResolver, SteamGameResolver>();

        services.AddSingleton<IFileDropProcessor, DefaultFileDropProcessor>();
        services.AddSingleton<IFileDropRequestHandler, FileDropRequestHandler>();
        services.AddSingleton<FileDropIpcServer>();
        services.AddSingleton<IFileDropIpcServer>(sp => sp.GetRequiredService<FileDropIpcServer>());
        services.AddSingleton<IHostedService>(sp => sp.GetRequiredService<FileDropIpcServer>());

        services.AddHostedService<Worker>();
    })
    .Build();

// Print effective config file path and build info
try
{
    var cfgProvider = host.Services.GetService<IConfigProvider>();
    if (cfgProvider is IConfigPathProvider pathProvider)
    {
        Console.WriteLine($"Using config: {pathProvider.ConfigPath}");
    }

    CommandHelpers.PrintBuildInfo(parsedArgs.EnableDebug);

    try
    {
        var autoStartManager = host.Services.GetRequiredService<IAutoStartManager>();
        if (autoStartManager.IsSupported)
        {
            var appConfigProvider = host.Services.GetRequiredService<IAppConfigProvider>();
            var appConfig = appConfigProvider.LoadAppConfig();
            autoStartManager.SetEnabled(appConfig.LaunchOnSystemStartup);
        }
    }
    catch (Exception ex)
    {
        Console.WriteLine($"Failed to apply auto-start preference: {ex.Message}");
    }

    // Handle file drag & drop (auto-add to config and exit)
    if (isFileDropRequest)
    {
        var handler = host.Services.GetRequiredService<IFileDropRequestHandler>();
        var response = await handler.HandleAsync(
                new DropAddRequest { Paths = parsedArgs.EffectiveArgs, ConfigOverride = parsedArgs.ConfigOverride },
                CancellationToken.None)
            .ConfigureAwait(false);

        var text = FormatDropResponse(response);
        FileDropHandler.TryShowMessageBox(text, "GameHelper");
        Environment.Exit(response.Success ? 0 : 1);
    }
}
catch (Exception ex)
{
    Console.WriteLine($"Auto-add failed: {ex.Message}");
    Environment.Exit(1);
}

// Execute the appropriate command
var interactiveMode = parsedArgs.UseInteractiveShell || parsedArgs.EffectiveArgs.Length == 0;
if (interactiveMode)
{
    var ipcServer = host.Services.GetRequiredService<IFileDropIpcServer>();
    await ipcServer.StartAsync(CancellationToken.None).ConfigureAwait(false);
    try
    {
        var shell = new InteractiveShell(host, parsedArgs);
        await shell.RunAsync();
    }
    finally
    {
        await ipcServer.StopAsync(CancellationToken.None).ConfigureAwait(false);
    }

    return;
}

var command = parsedArgs.EffectiveArgs[0].ToLowerInvariant();
switch (command)
{
    case "monitor":
        await host.RunAsync();
        break;

    case "config":
        ConfigCommand.Run(host.Services, parsedArgs.EffectiveArgs.Skip(1).ToArray());
        break;

    case "stats":
        StatsCommand.Run(host.Services, parsedArgs.EffectiveArgs.Skip(1).ToArray());
        break;

    case "convert-config":
        ConvertConfigCommand.Run();
        break;

    case "validate-config":
        ValidateConfigCommand.Run();
        break;

    case "migrate":
    case "migrate-config":
        MigrateCommand.Run(parsedArgs.EffectiveArgs.Skip(1).ToArray());
        break;

    case "interactive":
        var ipcServer = host.Services.GetRequiredService<IFileDropIpcServer>();
        await ipcServer.StartAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            var shell = new InteractiveShell(host, parsedArgs);
            await shell.RunAsync();
        }
        finally
        {
            await ipcServer.StopAsync(CancellationToken.None).ConfigureAwait(false);
        }

        break;

    default:
        CommandHelpers.PrintUsage();
        break;
}

static string FormatDropResponse(DropAddResponse response)
{
    if (!response.Success)
    {
        return $"添加失败: {response.Error}";
    }

    return $"已完成添加/更新\nAdded={response.Added}, Updated={response.Updated}, Skipped={response.Skipped}\n重复清理: {response.DuplicatesRemoved}\n配置: {response.ConfigPath}";
}
