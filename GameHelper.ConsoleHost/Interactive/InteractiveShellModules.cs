using System;
using System.Threading;
using System.Threading.Tasks;
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
        IGameConfiguration gameConfiguration,
        MonitorUI monitorUI,
        GameCatalogUI catalogUI,
        SettingsUI settingsUI,
        StatisticsUI statisticsUI,
        ToolsUI toolsUI)
    {
        Console = console;
        PromptUI = promptUI;
        GameConfiguration = gameConfiguration;
        MonitorUI = monitorUI;
        CatalogUI = catalogUI;
        SettingsUI = settingsUI;
        StatisticsUI = statisticsUI;
        ToolsUI = toolsUI;
    }

    public IAnsiConsole Console { get; }

    public PromptUI PromptUI { get; }

    public IGameConfiguration GameConfiguration { get; }

    public MonitorUI MonitorUI { get; }

    public GameCatalogUI CatalogUI { get; }

    public SettingsUI SettingsUI { get; }

    public StatisticsUI StatisticsUI { get; }

    public ToolsUI ToolsUI { get; }

    /// <summary>
    /// 交互界面的控制台实例。宿主（conhost / ConPTY）对 VT 的支持不一定能被 Spectre 的探测
    /// 识别到，探测失败会让整屏 markup 退化成灰白，所以真实终端上直接启用 ANSI 与真彩色；
    /// 输出被重定向时保持默认实例，避免把转义序列写进管道。
    /// </summary>
    private static IAnsiConsole CreateInteractiveConsole()
    {
        if (System.Console.IsOutputRedirected)
        {
            return AnsiConsole.Console;
        }

        return AnsiConsole.Create(new AnsiConsoleSettings
        {
            Ansi = AnsiSupport.Yes,
            ColorSystem = ColorSystemSupport.TrueColor,
        });
    }

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
        var resolvedConsole = console ?? CreateInteractiveConsole();
        if (console is null)
        {
            resolvedConsole.Profile.Capabilities.Unicode = true;
        }

        var gameConfiguration = host.Services.GetRequiredService<IGameConfiguration>();
        var autoStartManager = host.Services.GetRequiredService<IAutoStartManager>();
        var statisticsService = host.Services.GetRequiredService<IStatisticsService>();
        var gameCatalogService = host.Services.GetRequiredService<IGameCatalogService>();
        var steamGameResolver = host.Services.GetService<ISteamGameResolver>();
        var monitorControlService = host.Services.GetRequiredService<IMonitorControlService>();
        var promptUI = new PromptUI(resolvedConsole, script);
        var resolvedMonitorLoop = monitorLoop ?? ((_, _) => Task.CompletedTask);

        return new InteractiveShellModules(
            resolvedConsole,
            promptUI,
            gameConfiguration,
            new MonitorUI(host, resolvedConsole, promptUI, statisticsService, monitorControlService, script, resolvedMonitorLoop, arguments.MonitorDryRun),
            new GameCatalogUI(resolvedConsole, promptUI, gameConfiguration, autoStartManager, gameCatalogService, steamGameResolver),
            new SettingsUI(resolvedConsole, promptUI, gameConfiguration, autoStartManager),
            new StatisticsUI(resolvedConsole, promptUI, statisticsService),
            new ToolsUI(resolvedConsole, promptUI));
    }
}
