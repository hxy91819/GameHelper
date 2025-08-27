using System;
using System.Linq;
using GameHelper.ConsoleHost;
using GameHelper.ConsoleHost.Commands;
using GameHelper.ConsoleHost.Services;
using GameHelper.ConsoleHost.Utilities;
using GameHelper.Core.Abstractions;
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
        services.AddSingleton<IProcessMonitor>(sp =>
        {
            var cfg = sp.GetRequiredService<IConfigProvider>();
            var map = cfg.Load();
            // Use enabled entries as whitelist (keys are normalized executable names like "game.exe")
            var allowed = map is null
                ? Array.Empty<string>()
                : System.Linq.Enumerable.Select(
                    System.Linq.Enumerable.Where(map, kv => kv.Value?.IsEnabled == true),
                    kv => kv.Key);
            return new WmiProcessMonitor(allowed);
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
var command = parsedArgs.EffectiveArgs.Length > 0 ? parsedArgs.EffectiveArgs[0].ToLowerInvariant() : "monitor";
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

    default:
        CommandHelpers.PrintUsage();
        break;
}


