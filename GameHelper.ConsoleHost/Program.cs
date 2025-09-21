using System;
using System.Linq;
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
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

// Parse command line arguments
var parsedArgs = ArgumentParser.Parse(args);

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
                return new YamlConfigProvider(parsedArgs.ConfigOverride!);
            return new YamlConfigProvider();
        });
        services.AddSingleton<IAppConfigProvider>(sp => (YamlConfigProvider)sp.GetRequiredService<IConfigProvider>());
        services.AddSingleton<IProcessMonitor>(sp =>
        {
            var logger = sp.GetRequiredService<ILogger<Program>>();
            var appConfigProvider = sp.GetRequiredService<IAppConfigProvider>();
            var appConfig = appConfigProvider.LoadAppConfig();
            
            // Determine monitor type: command line > config file > default (WMI)
            ProcessMonitorType monitorType = ProcessMonitorType.WMI; // default
            
            if (!string.IsNullOrWhiteSpace(parsedArgs.MonitorType))
            {
                if (Enum.TryParse<ProcessMonitorType>(parsedArgs.MonitorType, true, out var cmdLineType))
                {
                    monitorType = cmdLineType;
                    logger.LogInformation("Using monitor type from command line: {MonitorType}", monitorType);
                }
                else
                {
                    logger.LogWarning("Invalid monitor type '{MonitorType}' specified in command line, using default WMI", parsedArgs.MonitorType);
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
            var enabledGames = gameConfigs
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
        services.AddSingleton<IHdrController, NoOpHdrController>();
        services.AddSingleton<IPlayTimeService, CsvBackedPlayTimeService>();
        services.AddSingleton<IGameAutomationService, GameAutomationService>();
        // Steam URL resolver for optional .url drag&drop support
        services.AddSingleton<ISteamGameResolver, SteamGameResolver>();
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

    // Handle file drag & drop (auto-add to config and exit)
    if (FileDropHandler.LooksLikeFilePaths(parsedArgs.EffectiveArgs))
    {
        var summary = FileDropHandler.ProcessFilePaths(parsedArgs.EffectiveArgs, parsedArgs.ConfigOverride, host.Services);
        var text = $"已完成添加/更新\nAdded={summary.Added}, Updated={summary.Updated}, Skipped={summary.Skipped}\n重复清理: {summary.DuplicatesRemoved}\n配置: {summary.ConfigPath}";
        FileDropHandler.TryShowMessageBox(text, "GameHelper");
        Environment.Exit(0);
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
    var shell = new InteractiveShell(host, parsedArgs);
    await shell.RunAsync();
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
        StatsCommand.Run(parsedArgs.EffectiveArgs.Skip(1).ToArray());
        break;

    case "convert-config":
        ConvertConfigCommand.Run();
        break;

    case "validate-config":
        ValidateConfigCommand.Run();
        break;

    case "interactive":
        var shell = new InteractiveShell(host, parsedArgs);
        await shell.RunAsync();
        break;

    default:
        CommandHelpers.PrintUsage();
        break;
}


