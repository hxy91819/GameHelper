using System;
using System.Threading;
using System.Threading.Tasks;
using GameHelper.ConsoleHost.Models;
using GameHelper.ConsoleHost.Utilities;
using GameHelper.Core.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Spectre.Console;

namespace GameHelper.ConsoleHost.Interactive;

internal sealed class InteractiveShellModules
{
    private InteractiveShellModules(
        IAnsiConsole console,
        PromptUI promptUI,
        IConfigProvider configProvider,
        IAppConfigProvider appConfigProvider,
        MonitorUI monitorUI,
        GameCatalogUI catalogUI,
        SettingsUI settingsUI,
        StatisticsUI statisticsUI,
        ToolsUI toolsUI)
    {
        Console = console;
        PromptUI = promptUI;
        ConfigProvider = configProvider;
        AppConfigProvider = appConfigProvider;
        MonitorUI = monitorUI;
        CatalogUI = catalogUI;
        SettingsUI = settingsUI;
        StatisticsUI = statisticsUI;
        ToolsUI = toolsUI;
    }

    public IAnsiConsole Console { get; }

    public PromptUI PromptUI { get; }

    public IConfigProvider ConfigProvider { get; }

    public IAppConfigProvider AppConfigProvider { get; }

    public MonitorUI MonitorUI { get; }

    public GameCatalogUI CatalogUI { get; }

    public SettingsUI SettingsUI { get; }

    public StatisticsUI StatisticsUI { get; }

    public ToolsUI ToolsUI { get; }

    public static InteractiveShellModules Create(
        IHost host,
        ParsedArguments arguments,
        IAnsiConsole? console,
        InteractiveScript? script,
        Func<IHost, CancellationToken, Task>? monitorLoop)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(arguments);

        ConsoleEncoding.EnsureUtf8();
        var resolvedConsole = console ?? AnsiConsole.Console;
        if (console is null)
        {
            resolvedConsole.Profile.Capabilities.Unicode = true;
        }

        var configProvider = host.Services.GetRequiredService<IConfigProvider>();
        var appConfigProvider = host.Services.GetRequiredService<IAppConfigProvider>();
        var autoStartManager = host.Services.GetRequiredService<IAutoStartManager>();
        var statisticsService = host.Services.GetRequiredService<IStatisticsService>();
        var gameCatalogService = host.Services.GetRequiredService<IGameCatalogService>();
        var monitorControlService = host.Services.GetRequiredService<IMonitorControlService>();
        var promptUI = new PromptUI(resolvedConsole, script);
        var resolvedMonitorLoop = monitorLoop ?? ((_, _) => Task.CompletedTask);

        return new InteractiveShellModules(
            resolvedConsole,
            promptUI,
            configProvider,
            appConfigProvider,
            new MonitorUI(host, resolvedConsole, promptUI, statisticsService, monitorControlService, script, resolvedMonitorLoop, arguments.MonitorDryRun),
            new GameCatalogUI(resolvedConsole, promptUI, configProvider, appConfigProvider, autoStartManager, gameCatalogService),
            new SettingsUI(resolvedConsole, promptUI, appConfigProvider, autoStartManager),
            new StatisticsUI(resolvedConsole, promptUI, statisticsService),
            new ToolsUI(resolvedConsole, promptUI));
    }
}
