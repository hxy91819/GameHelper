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
using GameHelper.Infrastructure.Startup;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

ConsoleEncoding.EnsureUtf8();

// Parse command line arguments
var parsedArgs = ArgumentParser.Parse(args);

// [DECISION] Smoke-test mode is checked BEFORE the single-instance guard so a
// real running instance does not block the probe, and the probe does not have
// to share a console with anyone. It is a one-shot self-check that exits with
// a status code and a single stdout line ("TRAY_OK" or "TRAY_FAIL: <reason>")
// the integration test can read.
if (parsedArgs.RunTraySmokeTest)
{
    var smokeExit = TrayIconSmokeTest.Run();
    Environment.Exit(smokeExit);
}

if (parsedArgs.RunHideSmokeTest)
{
    var hideExit = HideSmokeTest.Run();
    Environment.Exit(hideExit);
}

if (!ProcessInstanceGuard.TryClaim())
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
                return new YamlConfigProvider(parsedArgs.ConfigOverride!);
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
using var exitCts = new System.Threading.CancellationTokenSource();

TrayIconService? CreateTrayService()
{
    if (!OperatingSystem.IsWindows() || parsedArgs.DisableTray)
        return null;

    // [DECISION] With OutputType=WinExe, Windows does not allocate a console automatically.
    // AllocConsole creates our own conhost.exe window that we fully own. This avoids the
    // Windows Terminal hosting problem where GetConsoleWindow() returns the terminal's window.
    ConsoleWindowHelper.CreateConsole();

    var tray = new TrayIconService(host);
    tray.ExitRequested += () =>
    {
        // [DECISION] Spectre.Console .Prompt() is synchronous and does not accept CancellationToken,
        // so cooperative cancellation alone cannot interrupt a blocking prompt. We first try
        // graceful shutdown via exitCts.Cancel() (lets RunAsync exit between loop iterations),
        // then schedule a 3-second fallback to Environment.Exit(0) as a safety net.
        // TrayIconService.RequestExit already called Cleanup()+Application.Exit() on the UI
        // thread before firing this event, so the NotifyIcon is removed before we force-quit.
        // DO NOT remove Environment.Exit(0): without it the process hangs forever if the user
        // clicks "Exit" while Spectre.Console is blocking on input.
        try { exitCts.Cancel(); } catch { }
        _ = Task.Run(async () =>
        {
            await Task.Delay(TimeSpan.FromSeconds(3));
            Environment.Exit(0);
        });
    };
    tray.Initialize();
    // Hide the console window — SW_HIDE removes it from both screen and taskbar.
    ConsoleWindowHelper.Hide();
    return tray;
}

var interactiveMode = parsedArgs.UseInteractiveShell || parsedArgs.EffectiveArgs.Length == 0;
if (interactiveMode)
{
    using var trayService = CreateTrayService();
    // [DECISION] When tray is active, CreateTrayService() has already called
    // AllocConsole to own a dedicated conhost window that we can hide. When
    // tray is disabled (--no-tray or non-Windows), OutputType=Exe already
    // gave us a console, so we do NOT call AllocConsole again — doing so
    // would create a *second* conhost window that the user would have to
    // dismiss manually, defeating the "no extra black window" goal.
    var shell = new InteractiveShell(host, parsedArgs);
    await shell.RunAsync(exitCts.Token);
    return;
}

var command = parsedArgs.EffectiveArgs[0].ToLowerInvariant();
// [DECISION] OutputType=WinExe means no automatic console. Non-interactive subcommands
// still need console output, so allocate one for all subcommand paths.
ConsoleWindowHelper.CreateConsole();
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

    case "migrate":
    case "migrate-config":
        MigrateCommand.Run(parsedArgs.EffectiveArgs.Skip(1).ToArray());
        break;

    case "interactive":
        {
            using var traySvc = CreateTrayService();
            var interactiveShell = new InteractiveShell(host, parsedArgs);
            await interactiveShell.RunAsync(exitCts.Token);
        }
        break;

    default:
        CommandHelpers.PrintUsage();
        break;
}
