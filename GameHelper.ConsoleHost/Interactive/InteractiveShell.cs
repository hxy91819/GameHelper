using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using GameHelper.ConsoleHost.Models;
using GameHelper.ConsoleHost.Services;
using GameHelper.ConsoleHost.Utilities;
using GameHelper.Core.Abstractions;
using GameHelper.Core.Models;
using GameHelper.Core.Utilities;
using GameHelper.Infrastructure.Providers;
using GameHelper.Infrastructure.Validators;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Spectre.Console;
using ConsoleValidationResult = Spectre.Console.ValidationResult;

namespace GameHelper.ConsoleHost.Interactive
{
    /// <summary>
    /// Provides a guided, multi-step interactive experience for the console host.
    /// </summary>
    public sealed class InteractiveShell
    {
        private enum MainMenuAction
        {
            Monitor,
            Configuration,
            Settings,
            Statistics,
            Tools,
            Exit
        }

        private enum ConfigAction
        {
            View,
            Add,
            Edit,
            Remove,
            Back
        }

        private enum SettingsAction
        {
            ToggleMonitorAutoStart,
            ToggleLaunchOnStartup,
            Back
        }

        private enum ToolAction
        {
            ConvertConfig,
            ValidateConfig,
            Back
        }

        private const int DirectNumberPollingMilliseconds = 25;
        private static readonly TimeSpan NumericSelectionIdleTimeout = TimeSpan.FromMilliseconds(500);

        private readonly IHost _host;
        private readonly ParsedArguments _arguments;
        private readonly IAnsiConsole _console;
        private readonly IConfigProvider _configProvider;
        private readonly IAppConfigProvider _appConfigProvider;
        private readonly IAutoStartManager _autoStartManager;
        private readonly InteractiveScript? _script;
        private readonly Func<IHost, CancellationToken, Task> _monitorLoop;
        private readonly bool _autoStartMonitor;

        public InteractiveShell(IHost host, ParsedArguments arguments, IAnsiConsole? console = null, InteractiveScript? script = null, Func<IHost, CancellationToken, Task>? monitorLoop = null)
        {
            _host = host ?? throw new ArgumentNullException(nameof(host));
            _arguments = arguments ?? throw new ArgumentNullException(nameof(arguments));
            ConsoleEncoding.EnsureUtf8();
            _console = console ?? AnsiConsole.Console;
            if (console is null)
            {
                _console.Profile.Capabilities.Unicode = true;
            }
            _configProvider = host.Services.GetRequiredService<IConfigProvider>();
            _appConfigProvider = host.Services.GetRequiredService<IAppConfigProvider>();
            _autoStartManager = host.Services.GetRequiredService<IAutoStartManager>();
            _script = script;
            _monitorLoop = monitorLoop ?? ((_, _) => Task.CompletedTask);
            _autoStartMonitor = DetermineAutoStartPreference();
        }

        public async Task RunAsync()
        {
            ConsoleEncoding.EnsureUtf8();
            try
            {
                Console.Title = "GameHelper 互动命令行";
            }
            catch
            {
                // Some environments (e.g., CI, redirected output) do not allow setting the console title.
            }

            RenderWelcome();

            var autoStartPending = _autoStartMonitor;

            while (true)
            {
                MainMenuAction action;

                if (autoStartPending)
                {
                    autoStartPending = false;
                    _console.MarkupLine("[grey]检测到配置开启自动启动，将直接进入实时监控。[/]");
                    _console.WriteLine();
                    action = MainMenuAction.Monitor;
                }
                else
                {
                    action = PromptMainMenu();
                }

                switch (action)
                {
                    case MainMenuAction.Monitor:
                        await LaunchMonitorAsync().ConfigureAwait(false);
                        break;

                    case MainMenuAction.Configuration:
                        await HandleConfigurationAsync().ConfigureAwait(false);
                        break;

                    case MainMenuAction.Settings:
                        await HandleSettingsAsync().ConfigureAwait(false);
                        break;

                    case MainMenuAction.Statistics:
                        ShowStatistics();
                        break;

                    case MainMenuAction.Tools:
                        HandleTools();
                        break;

                    case MainMenuAction.Exit:
                        _console.MarkupLine("[grey]再见，祝你游戏愉快！[/]");
                        return;
                }
            }
        }

        private bool DetermineAutoStartPreference()
        {
            try
            {
                var appConfig = _appConfigProvider.LoadAppConfig();
                return appConfig.AutoStartInteractiveMonitor;
            }
            catch
            {
                return false;
            }
        }

        private void RenderWelcome()
        {
            try
            {
                _console.Clear();
            }
            catch (IOException)
            {
                // Ignore clear failures in test environments or redirected output
            }

            var title = new FigletText("GameHelper")
            {
                Color = Color.Cyan1,
                Justification = Justify.Center
            };
            _console.Write(title);

            var highlight = new Grid();
            highlight.AddColumn(new GridColumn().NoWrap().PadLeft(0).PadRight(1));
            highlight.AddColumn(new GridColumn().NoWrap().PadLeft(0).PadRight(0));
            highlight.AddRow(new Markup("[yellow]💡[/]"), new Markup("[bold yellow]让流程更轻松[/]：实时监控、自动 HDR、游戏时长统计"));
            highlight.AddRow(new Markup("[yellow]⚙️[/]"), new Markup("[bold yellow]快速管理配置[/]：添加/修改/删除游戏，支持别名与 HDR 设置"));
            highlight.AddRow(new Markup("[yellow]🧪[/]"), new Markup("[bold yellow]诊断工具[/]：一键转换旧版配置并验证当前 YAML"));

            var panel = new Panel(highlight)
            {
                Header = new PanelHeader("功能概览"),
                Border = BoxBorder.Rounded,
                BorderStyle = new Style(Color.Grey)
            };
            _console.Write(panel);

            var infoTable = new Table { Border = TableBorder.Rounded };
            infoTable.AddColumn(new TableColumn("当前上下文").Centered());
            infoTable.AddColumn(new TableColumn("详情"));
            infoTable.AddRow("配置文件", GetConfigPathDescription());
            infoTable.AddRow("日志级别", _arguments.EnableDebug ? "Debug（命令行启用）" : "Information");
            infoTable.AddRow("监控模式", GetMonitorModeDescription());
            infoTable.AddRow("Version", GetVersionDescription());
            infoTable.AddRow("Build Time", GetBuildTimeDescription());
            infoTable.Caption("输入序号或使用方向键选择功能，回车确认");
            _console.Write(infoTable);

            _console.WriteLine();
        }

        private MainMenuAction PromptMainMenu()
        {
            var title = "[bold green]请选择要执行的操作：[/]";
            var choices = Enum.GetValues<MainMenuAction>();
            var prompt = new SelectionPrompt<MainMenuAction>
            {
                PageSize = 6
            };
            prompt.Title(title);
            prompt.AddChoices(choices);

            return PromptSelection(
                prompt,
                choices,
                    action => action switch
                    {
                        MainMenuAction.Monitor => "🚀  启动实时监控",
                        MainMenuAction.Configuration => "🛠   管理游戏配置",
                        MainMenuAction.Settings => "⚙️   全局设置",
                        MainMenuAction.Statistics => "📊  查看游戏时长统计",
                        MainMenuAction.Tools => "🧰  工具与诊断",
                        MainMenuAction.Exit => "⬅️   退出",
                    _ => action.ToString()
                },
                title);
        }

        private async Task LaunchMonitorAsync()
        {
            var snapshotBefore = CaptureSessionSnapshot();
            var dryRun = _arguments.MonitorDryRun;

            var monitorRule = new Rule("[yellow]实时监控[/]")
            {
                Style = new Style(Color.Grey),
                Justification = Justify.Left
            };
            _console.Write(monitorRule);

            var monitorInfo = new Grid();
            monitorInfo.AddColumn(new GridColumn().NoWrap());
            monitorInfo.AddRow(new Markup($"将以 [bold]{Markup.Escape(GetMonitorModeDescription())}[/] 运行监控"));
            monitorInfo.AddRow(new Markup("开始后可按 [bold]Q[/] 键停止并返回主菜单"));
            monitorInfo.AddRow(new Markup($"配置文件位置：{Markup.Escape(GetConfigPathDescription())}"));
            monitorInfo.AddRow(new Markup("后台服务会自动加载启用的游戏列表进行白名单监控"));
            if (dryRun)
            {
                monitorInfo.AddRow(new Markup("[yellow]Dry-run 模式：不会启动后台监控服务。[/]"));
            }

            _console.Write(new Panel(monitorInfo)
            {
                Header = new PanelHeader("执行前确认"),
                Border = BoxBorder.Rounded
            });

            RenderMonitorHistory(snapshotBefore);
            _console.WriteLine();

            _console.MarkupLine("[bold green]正在启动监控... 按 Q 键可随时返回主菜单。[/]");
            _console.WriteLine();

            IProcessMonitor? monitor = null;
            IGameAutomationService? automation = null;
            if (!dryRun)
            {
                monitor = _host.Services.GetRequiredService<IProcessMonitor>();
                automation = _host.Services.GetRequiredService<IGameAutomationService>();
            }

            using var monitorCts = new CancellationTokenSource();
            Task monitorLoopTask = Task.CompletedTask;
            var automationStarted = false;
            var monitorStarted = false;
            var started = false;
            var exitSignalled = false;
            Exception? startException = null;
            Exception? runException = null;

            try
            {
                if (!dryRun)
                {
                    automation!.Start();
                    automationStarted = true;
                    monitor!.Start();
                    monitorStarted = true;
                }
                started = true;

                monitorLoopTask = _monitorLoop(_host, monitorCts.Token);

                if (!dryRun)
                {
                    await WaitForMonitorExitAsync(monitorCts.Token).ConfigureAwait(false);
                }

                exitSignalled = true;
            }
            catch (OperationCanceledException)
            {
                exitSignalled = true;
            }
            catch (Exception ex)
            {
                if (!started)
                {
                    startException = ex;
                }
                else
                {
                    runException = ex;
                }
            }
            finally
            {
                monitorCts.Cancel();

                try
                {
                    await monitorLoopTask.ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                }
                catch (Exception ex)
                {
                    runException ??= ex;
                }

                if (monitorStarted && monitor is not null)
                {
                    try
                    {
                        monitor.Stop();
                    }
                    catch (Exception ex)
                    {
                        runException ??= ex;
                    }
                }

                if (automationStarted && automation is not null)
                {
                    try
                    {
                        automation.Stop();
                    }
                    catch (Exception ex)
                    {
                        runException ??= ex;
                    }
                }
            }

            if (startException != null)
            {
                _console.MarkupLine($"[red]启动监控失败：{Markup.Escape(startException.Message)}[/]");
                _console.WriteLine();
                return;
            }

            if (runException != null)
            {
                var message = exitSignalled
                    ? $"监控已停止，但处理过程中出现异常：{runException.Message}"
                    : $"监控过程中出现异常：{runException.Message}";
                _console.MarkupLine($"[red]{Markup.Escape(message)}[/]");
                _console.WriteLine();
                return;
            }

            _console.MarkupLine("[grey]监控已停止，正在汇总本次游玩...[/]");
            _console.WriteLine();

            var snapshotAfter = CaptureSessionSnapshot();
            RenderSessionSummary(snapshotBefore, snapshotAfter);
            _console.WriteLine();
        }

        private async Task WaitForMonitorExitAsync(CancellationToken cancellationToken)
        {
            if (_script != null && _script.TryPeek<string>(out var scriptedCommand) && IsQuitCommand(scriptedCommand))
            {
                _script.TryDequeue(out string _);
                return;
            }

            if (Console.IsInputRedirected)
            {
                await WaitForExitByPromptAsync(cancellationToken).ConfigureAwait(false);
                return;
            }

            var cancelSignal = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            ConsoleCancelEventHandler handler = (_, args) =>
            {
                args.Cancel = true;
                cancelSignal.TrySetResult(true);
            };

            Console.CancelKeyPress += handler;
            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    if (cancelSignal.Task.IsCompleted)
                    {
                        return;
                    }

                    try
                    {
                        if (Console.KeyAvailable)
                        {
                            var key = Console.ReadKey(intercept: true);
                            if (key.Key == ConsoleKey.Q)
                            {
                                return;
                            }
                        }
                    }
                    catch (InvalidOperationException)
                    {
                        await WaitForExitByPromptAsync(cancellationToken).ConfigureAwait(false);
                        return;
                    }
                    catch (IOException)
                    {
                        await WaitForExitByPromptAsync(cancellationToken).ConfigureAwait(false);
                        return;
                    }
                    catch (PlatformNotSupportedException)
                    {
                        await WaitForExitByPromptAsync(cancellationToken).ConfigureAwait(false);
                        return;
                    }

                    await Task.Delay(100).ConfigureAwait(false);
                }
            }
            finally
            {
                Console.CancelKeyPress -= handler;
            }
        }

        private Task WaitForExitByPromptAsync(CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var prompt = new TextPrompt<string>("输入 [bold]Q[/] 并按 Enter 返回主菜单")
                    .AllowEmpty()
                    .DefaultValue(string.Empty);

                var input = Prompt(prompt);
                if (IsQuitCommand(input))
                {
                    break;
                }

                _console.MarkupLine("[yellow]请输入 Q 键以结束监控。[/]");
            }

            return Task.CompletedTask;
        }

        private static bool IsQuitCommand(string? value)
        {
            return !string.IsNullOrWhiteSpace(value)
                && string.Equals(value.Trim(), "q", StringComparison.OrdinalIgnoreCase);
        }

        private async Task HandleConfigurationAsync()
        {
            while (true)
            {
                var title = "[bold green]配置管理[/]";
                var choices = Enum.GetValues<ConfigAction>();
                var prompt = new SelectionPrompt<ConfigAction>
                {
                    PageSize = 5
                };
                prompt.Title(title);
                prompt.AddChoices(choices);

                var selection = PromptSelection(
                    prompt,
                    choices,
                    action => action switch
                    {
                        ConfigAction.View => "📋  查看当前配置",
                        ConfigAction.Add => "➕  添加新游戏",
                        ConfigAction.Edit => "✏️  修改现有游戏",
                        ConfigAction.Remove => "🗑  删除游戏",
                        ConfigAction.Back => "⬅️  返回上一级",
                        _ => action.ToString()
                    },
                    title);
                switch (selection)
                {
                    case ConfigAction.View:
                        RenderConfigTable();
                        break;
                    case ConfigAction.Add:
                        await AddGameAsync().ConfigureAwait(false);
                        break;
                    case ConfigAction.Edit:
                        await EditGameAsync().ConfigureAwait(false);
                        break;
                    case ConfigAction.Remove:
                        await RemoveGameAsync().ConfigureAwait(false);
                        break;
                    case ConfigAction.Back:
                        return;
                }
            }
        }

        private async Task HandleSettingsAsync()
        {
            while (true)
            {
                var actions = new List<SettingsAction> { SettingsAction.ToggleMonitorAutoStart };
                if (_autoStartManager.IsSupported)
                {
                    actions.Add(SettingsAction.ToggleLaunchOnStartup);
                }
                actions.Add(SettingsAction.Back);

                var title = "[bold green]全局设置[/]";
                var prompt = new SelectionPrompt<SettingsAction>
                {
                    PageSize = actions.Count
                };
                prompt.Title(title);
                prompt.AddChoices(actions);

                var selection = PromptSelection(
                    prompt,
                    actions,
                    action => action switch
                    {
                        SettingsAction.ToggleMonitorAutoStart => "⚡️  启动后自动进入实时监控",
                        SettingsAction.ToggleLaunchOnStartup when _autoStartManager.IsSupported => "🖥️  设置开机自启动",
                        SettingsAction.ToggleLaunchOnStartup => "🖥️  开机自启动（当前环境不支持）",
                        SettingsAction.Back => "⬅️  返回上一级",
                        _ => action.ToString()
                    },
                    title);

                switch (selection)
                {
                    case SettingsAction.ToggleMonitorAutoStart:
                        await ConfigureMonitorAutoStartAsync().ConfigureAwait(false);
                        break;

                    case SettingsAction.ToggleLaunchOnStartup:
                        if (_autoStartManager.IsSupported)
                        {
                            await ConfigureSystemAutoStartAsync().ConfigureAwait(false);
                        }
                        else
                        {
                            _console.MarkupLine("[yellow]当前平台不支持开机自启动设置。[/]");
                        }
                        break;

                    case SettingsAction.Back:
                        return;
                }
            }
        }

        private void RenderConfigTable()
        {
            var configs = LoadConfigs();
            AppConfig? appConfig = null;
            try
            {
                appConfig = _appConfigProvider.LoadAppConfig();
            }
            catch (Exception ex)
            {
                _console.MarkupLine($"[red]无法加载全局配置：{Markup.Escape(ex.Message)}[/]");
            }
            var configRule = new Rule("[yellow]当前配置[/]")
            {
                Style = new Style(Color.Grey),
                Justification = Justify.Left
            };
            _console.Write(configRule);

            if (configs.Count == 0)
            {
                _console.MarkupLine("[italic grey]当前没有配置任何游戏，马上添加一个吧！[/]");
                return;
            }

            var table = new Table { Border = TableBorder.Rounded };
            table.AddColumn("DataKey");
            table.AddColumn("可执行文件");
            table.AddColumn("显示名称");
            table.AddColumn("路径");
            table.AddColumn("自动化");
            table.AddColumn("HDR");

            foreach (var cfg in configs.Values.OrderBy(e => e.DataKey, StringComparer.OrdinalIgnoreCase))
            {
                var pathDisplay = string.IsNullOrWhiteSpace(cfg.ExecutablePath) 
                    ? "-" 
                    : (cfg.ExecutablePath.Length > 30 
                        ? "..." + cfg.ExecutablePath.Substring(cfg.ExecutablePath.Length - 27) 
                        : cfg.ExecutablePath);
                
                table.AddRow(
                    Markup.Escape(cfg.DataKey),
                    Markup.Escape(cfg.ExecutableName ?? "-"),
                    string.IsNullOrWhiteSpace(cfg.DisplayName) ? "-" : Markup.Escape(cfg.DisplayName!),
                    Markup.Escape(pathDisplay),
                    cfg.IsEnabled ? "[green]启用[/]" : "[red]禁用[/]",
                    cfg.HDREnabled ? "[green]开启[/]" : "[yellow]保持关闭[/]");
            }

            _console.Write(table);

            if (appConfig != null)
            {
                var autoStartState = appConfig.AutoStartInteractiveMonitor
                    ? "[green]启动后自动进入实时监控[/]"
                    : "[yellow]启动后需要手动选择监控[/]";
                _console.WriteLine();
                _console.MarkupLine($"自动监控：{autoStartState}");

                var launchSegments = new List<string>
                {
                    appConfig.LaunchOnSystemStartup
                        ? "[green]开机时自动启动 GameHelper[/]"
                        : "[yellow]开机时不会自动启动[/]"
                };

                if (!_autoStartManager.IsSupported)
                {
                    launchSegments.Add("（当前环境不支持自动配置）");
                }
                else
                {
                    var systemState = TryReadSystemAutoStartState();
                    if (systemState.HasValue)
                    {
                        var systemSegment = systemState.Value
                            ? "[green]系统状态：已启用[/]"
                            : "[yellow]系统状态：未启用[/]";

                        if (systemState.Value != appConfig.LaunchOnSystemStartup)
                        {
                            systemSegment += "[red]（与配置不一致）[/]";
                        }

                        launchSegments.Add($"（{systemSegment}）");
                    }
                }

                var launchState = string.Concat(launchSegments);
                _console.MarkupLine($"开机自启动：{launchState}");
            }
        }

        private async Task ConfigureMonitorAutoStartAsync()
        {
            AppConfig appConfig;
            try
            {
                appConfig = _appConfigProvider.LoadAppConfig();
            }
            catch (Exception ex)
            {
                _console.MarkupLine($"[red]加载全局配置失败：{Markup.Escape(ex.Message)}[/]");
                return;
            }

            var current = appConfig.AutoStartInteractiveMonitor;
            var enableOption = current ? "保持自动启动" : "开启自动启动";
            var disableOption = current ? "改为手动启动" : "保持手动启动";
            var options = new[] { enableOption, disableOption };

            var title = "启动后是否自动进入实时监控？";
            var prompt = new SelectionPrompt<string>();
            prompt.Title(title);
            prompt.AddChoices(options);

            var selection = PromptSelection(prompt, options, value => Markup.Escape(value), title);
            var newValue = string.Equals(selection, enableOption, StringComparison.Ordinal);

            if (newValue == current)
            {
                _console.MarkupLine("[grey]设置保持不变。[/]");
                return;
            }

            appConfig.AutoStartInteractiveMonitor = newValue;

            try
            {
                await Task.Run(() => _appConfigProvider.SaveAppConfig(appConfig)).ConfigureAwait(false);
                var resultMessage = newValue
                    ? "[green]已更新：启动后将自动进入实时监控。[/]"
                    : "[green]已更新：启动后需手动选择监控。[/]";
                _console.MarkupLine(resultMessage);
            }
            catch (Exception ex)
            {
                _console.MarkupLine($"[red]保存配置失败：{Markup.Escape(ex.Message)}[/]");
            }
        }

        private async Task ConfigureSystemAutoStartAsync()
        {
            if (!_autoStartManager.IsSupported)
            {
                _console.MarkupLine("[yellow]当前平台不支持开机自启动设置。[/]");
                return;
            }

            AppConfig appConfig;
            try
            {
                appConfig = _appConfigProvider.LoadAppConfig();
            }
            catch (Exception ex)
            {
                _console.MarkupLine($"[red]加载全局配置失败：{Markup.Escape(ex.Message)}[/]");
                return;
            }

            var currentConfigValue = appConfig.LaunchOnSystemStartup;

            bool? systemState = null;
            if (_autoStartManager.IsSupported)
            {
                systemState = TryReadSystemAutoStartState();
                if (systemState.HasValue)
                {
                    var status = systemState.Value
                        ? "[green]系统当前已启用。[/]"
                        : "[yellow]系统当前未启用。[/]";

                    if (systemState.Value != currentConfigValue)
                    {
                        status += "[yellow]（与配置记录不一致）[/]";
                    }

                    _console.MarkupLine(status);
                }
            }

            var enableOption = currentConfigValue ? "保持开机自启动" : "开启开机自启动";
            var disableOption = currentConfigValue ? "关闭开机自启动" : "保持不开启";
            var options = new[] { enableOption, disableOption };

            var title = "是否在开机时自动启动 GameHelper？";
            var prompt = new SelectionPrompt<string>();
            prompt.Title(title);
            prompt.AddChoices(options);

            var selection = PromptSelection(prompt, options, value => Markup.Escape(value), title);
            var newValue = string.Equals(selection, enableOption, StringComparison.Ordinal);

            var needsSystemUpdate = !systemState.HasValue || systemState.Value != newValue;
            var needsConfigUpdate = newValue != currentConfigValue;

            if (!needsSystemUpdate && !needsConfigUpdate)
            {
                _console.MarkupLine("[grey]设置保持不变。[/]");
                return;
            }

            if (needsSystemUpdate)
            {
                try
                {
                    _autoStartManager.SetEnabled(newValue);
                }
                catch (Exception ex)
                {
                    _console.MarkupLine($"[red]更新系统自启动设置失败：{Markup.Escape(ex.Message)}[/]");
                    return;
                }
            }

            if (needsConfigUpdate)
            {
                appConfig.LaunchOnSystemStartup = newValue;

                try
                {
                    await Task.Run(() => _appConfigProvider.SaveAppConfig(appConfig)).ConfigureAwait(false);
                    var resultMessage = newValue
                        ? "[green]已更新：开机时将自动启动 GameHelper。[/]"
                        : "[green]已更新：开机时不会自动启动 GameHelper。[/]";
                    _console.MarkupLine(resultMessage);
                }
                catch (Exception ex)
                {
                    _console.MarkupLine($"[red]保存配置失败：{Markup.Escape(ex.Message)}[/]");
                }
            }
            else
            {
                var ensureMessage = newValue
                    ? "[green]已确保系统开机自启动处于开启状态。[/]"
                    : "[green]已确保系统开机自启动处于关闭状态。[/]";
                _console.MarkupLine(ensureMessage);
            }
        }

        private bool? TryReadSystemAutoStartState()
        {
            try
            {
                return _autoStartManager.IsEnabled();
            }
            catch (Exception ex)
            {
                _console.MarkupLine($"[yellow]无法读取系统自启动状态：{Markup.Escape(ex.Message)}[/]");
                return null;
            }
        }

        private async Task AddGameAsync()
        {
            var configs = LoadConfigs();

            // Prompt for input - can be file path or executable name
            var inputPrompt = new TextPrompt<string>(
                "请输入游戏的可执行文件名或拖放 EXE/LNK 文件\n" +
                "(例如 [green]game.exe[/] 或完整路径 [green]C:\\Games\\game.exe[/])")
                .Validate(input => string.IsNullOrWhiteSpace(input)
                    ? ConsoleValidationResult.Error("输入不能为空。")
                    : ConsoleValidationResult.Success());

            var input = Prompt(inputPrompt);
            input = input.Trim().Trim('"'); // Remove quotes if dragged

            string? exePath = null;
            string executableName;
            string? productName = null;
            string? suggestedDataKey;

            // Check if input is a file path
            if (File.Exists(input))
            {
                var ext = Path.GetExtension(input).ToLowerInvariant();
                
                if (ext == ".lnk")
                {
                    // Resolve shortcut
                    exePath = ExecutableResolver.TryResolveFromInput(input);
                    if (string.IsNullOrWhiteSpace(exePath))
                    {
                        _console.MarkupLine("[red]无法解析快捷方式目标。[/]");
                        return;
                    }
                    _console.MarkupLine($"[grey]已解析快捷方式目标：{Markup.Escape(exePath)}[/]");
                }
                else if (ext == ".exe")
                {
                    exePath = input;
                }
                else
                {
                    _console.MarkupLine("[yellow]不支持的文件类型。请提供 .exe 或 .lnk 文件。[/]");
                    return;
                }

                // Extract metadata
                (productName, _) = GameMetadataExtractor.ExtractMetadata(exePath);
                executableName = Path.GetFileName(exePath);
                suggestedDataKey = DataKeyGenerator.GenerateUniqueDataKey(exePath, productName, _configProvider);

                // Display extracted information
                _console.MarkupLine("[green]检测到游戏文件：[/]");
                _console.MarkupLine($"  路径: {Markup.Escape(exePath)}");
                _console.MarkupLine($"  可执行文件名: {Markup.Escape(executableName)}");
                if (!string.IsNullOrWhiteSpace(productName))
                {
                    _console.MarkupLine($"  产品名称: {Markup.Escape(productName)}");
                }
                _console.WriteLine();
            }
            else
            {
                // Treat as executable name only
                executableName = input;
                suggestedDataKey = DataKeyGenerator.GenerateBaseDataKey(input);
            }

            // Check for existing config by path first, then by unique executable name fallback.
            var existingConfig = ConfigEntryMatcher.FindExistingForAdd(configs.Values, executableName, exePath);
            var existingEntryId = existingConfig?.EntryId;

            // Prompt for DataKey
            var dataKeyPrompt = new TextPrompt<string>(
                $"请输入 DataKey（用于数据关联的唯一标识符）\n" +
                $"建议值: [green]{Markup.Escape(suggestedDataKey)}[/]")
                .AllowEmpty()
                .DefaultValue(suggestedDataKey)
                .Validate(key =>
                {
                    if (string.IsNullOrWhiteSpace(key))
                    {
                        return ConsoleValidationResult.Error("DataKey 不能为空。");
                    }
                    // Check uniqueness (excluding current entry if updating)
                    if (configs.Values.Any(c => 
                        string.Equals(c.DataKey, key, StringComparison.OrdinalIgnoreCase) && 
                        !string.Equals(c.EntryId, existingEntryId, StringComparison.OrdinalIgnoreCase)))
                    {
                        return ConsoleValidationResult.Error($"DataKey '{key}' 已被其他游戏使用。");
                    }
                    return ConsoleValidationResult.Success();
                });

            var dataKey = Prompt(dataKeyPrompt);

            // Prompt for DisplayName
            var defaultDisplayName = existingConfig != null && !string.IsNullOrWhiteSpace(existingConfig.DisplayName)
                ? existingConfig.DisplayName!
                : productName ?? string.Empty;
            
            var displayNamePrompt = new TextPrompt<string>("输入显示名称（可选，直接回车跳过）")
                .AllowEmpty()
                .DefaultValue(defaultDisplayName);
            var displayName = Prompt(displayNamePrompt);

            // Prompt for automation enable
            var enableTitle = "是否启用自动化？";
            var enableChoices = existingConfig?.IsEnabled == false
                ? new[] { "禁用", "启用" }
                : new[] { "启用", "禁用" };
            var enablePrompt = new SelectionPrompt<string>();
            enablePrompt.Title(enableTitle);
            enablePrompt.AddChoices(enableChoices);
            var enable = PromptSelection(enablePrompt, enableChoices, value => Markup.Escape(value), enableTitle);

            // Prompt for HDR
            var hdrTitle = "在游戏运行时如何控制 HDR？";
            var defaultHdrEnabled = existingConfig?.HDREnabled ?? false;
            var hdrChoices = defaultHdrEnabled
                ? new[] { "自动开启 HDR", "保持关闭" }
                : new[] { "保持关闭", "自动开启 HDR" };
            var hdrPrompt = new SelectionPrompt<string>();
            hdrPrompt.Title(hdrTitle);
            hdrPrompt.AddChoices(hdrChoices);
            var hdr = PromptSelection(hdrPrompt, hdrChoices, value => Markup.Escape(value), hdrTitle);

            // Create or update config
            var entryId = string.IsNullOrWhiteSpace(existingEntryId)
                ? Guid.NewGuid().ToString("N")
                : existingEntryId;

            configs[entryId] = new GameConfig
            {
                EntryId = entryId,
                DataKey = dataKey,
                ExecutablePath = exePath, // Will be null if only name was provided
                ExecutableName = executableName,
                DisplayName = string.IsNullOrWhiteSpace(displayName) ? null : displayName.Trim(),
                IsEnabled = string.Equals(enable, "启用", StringComparison.Ordinal),
                HDREnabled = string.Equals(hdr, "自动开启 HDR", StringComparison.Ordinal)
            };

            await PersistAsync(configs).ConfigureAwait(false);
            _console.MarkupLine($"[green]已保存[/]：{Markup.Escape(executableName)} (DataKey: {Markup.Escape(dataKey)})");
        }

        private async Task EditGameAsync()
        {
            var configs = LoadConfigs();
            if (configs.Count == 0)
            {
                _console.MarkupLine("[italic grey]没有可以修改的游戏。[/]");
                return;
            }

            var title = "选择需要修改的游戏";
            var choices = configs.Values
                .Select(c => c.DataKey)
                .OrderBy(k => k, StringComparer.OrdinalIgnoreCase)
                .ToList();
            var prompt = new SelectionPrompt<string>
            {
                PageSize = 10
            };
            prompt.Title(title);
            prompt.AddChoices(choices);

            var selectedDataKey = PromptSelection(prompt, choices, value => Markup.Escape(value), title);
            var selected = configs.FirstOrDefault(kv => string.Equals(kv.Value.DataKey, selectedDataKey, StringComparison.OrdinalIgnoreCase));
            var entryId = selected.Key;
            var cfg = selected.Value;
            if (cfg is null || string.IsNullOrWhiteSpace(entryId))
            {
                _console.MarkupLine("[red]未找到对应的配置。[/]");
                return;
            }

            // Display current configuration
            _console.MarkupLine("[yellow]当前配置：[/]");
            _console.MarkupLine($"  DataKey: {Markup.Escape(cfg.DataKey)}");
            _console.MarkupLine($"  可执行文件名: {Markup.Escape(cfg.ExecutableName ?? "-")}");
            if (!string.IsNullOrWhiteSpace(cfg.ExecutablePath))
            {
                _console.MarkupLine($"  完整路径: {Markup.Escape(cfg.ExecutablePath)}");
            }
            if (!string.IsNullOrWhiteSpace(cfg.DisplayName))
            {
                _console.MarkupLine($"  显示名称: {Markup.Escape(cfg.DisplayName)}");
            }
            _console.WriteLine();

            // Prompt for ExecutablePath update
            var pathPrompt = new TextPrompt<string>(
                "更新可执行文件路径（可选，直接回车保持不变）\n" +
                "可以拖放 EXE 或 LNK 文件")
                .AllowEmpty()
                .DefaultValue(cfg.ExecutablePath ?? string.Empty);
            var pathInput = Prompt(pathPrompt);
            pathInput = pathInput.Trim().Trim('"');

            string? newExecutablePath = cfg.ExecutablePath;
            if (!string.IsNullOrWhiteSpace(pathInput) && pathInput != cfg.ExecutablePath)
            {
                if (File.Exists(pathInput))
                {
                    var ext = Path.GetExtension(pathInput).ToLowerInvariant();
                    if (ext == ".lnk")
                    {
                        newExecutablePath = ExecutableResolver.TryResolveFromInput(pathInput);
                        if (string.IsNullOrWhiteSpace(newExecutablePath))
                        {
                            _console.MarkupLine("[red]无法解析快捷方式目标，保持原路径不变。[/]");
                            newExecutablePath = cfg.ExecutablePath;
                        }
                        else
                        {
                            _console.MarkupLine($"[grey]已解析快捷方式目标：{Markup.Escape(newExecutablePath)}[/]");
                        }
                    }
                    else if (ext == ".exe")
                    {
                        newExecutablePath = pathInput;
                    }
                    else
                    {
                        _console.MarkupLine("[yellow]不支持的文件类型，保持原路径不变。[/]");
                    }
                }
                else if (string.Equals(pathInput, "clear", StringComparison.OrdinalIgnoreCase) ||
                         string.Equals(pathInput, "remove", StringComparison.OrdinalIgnoreCase))
                {
                    newExecutablePath = null;
                    _console.MarkupLine("[grey]已清除可执行文件路径。[/]");
                }
                else
                {
                    _console.MarkupLine("[yellow]文件不存在，保持原路径不变。输入 'clear' 可清除路径。[/]");
                }
            }

            // Prompt for DisplayName update
            var displayNamePrompt = new TextPrompt<string>("更新显示名称（可留空）")
                .AllowEmpty()
                .DefaultValue(cfg.DisplayName ?? string.Empty);
            var displayName = Prompt(displayNamePrompt);

            // Prompt for automation enable
            var enableTitle = "是否启用自动化？";
            var enableChoices = cfg.IsEnabled
                ? new[] { "启用", "禁用" }
                : new[] { "禁用", "启用" };
            var enablePrompt = new SelectionPrompt<string>();
            enablePrompt.Title(enableTitle);
            enablePrompt.AddChoices(enableChoices);
            var enable = PromptSelection(enablePrompt, enableChoices, value => Markup.Escape(value), enableTitle);

            // Prompt for HDR
            var hdrTitle = "在游戏运行时如何控制 HDR？";
            var hdrChoices = cfg.HDREnabled
                ? new[] { "自动开启 HDR", "保持关闭" }
                : new[] { "保持关闭", "自动开启 HDR" };
            var hdrPrompt = new SelectionPrompt<string>();
            hdrPrompt.Title(hdrTitle);
            hdrPrompt.AddChoices(hdrChoices);
            var hdr = PromptSelection(hdrPrompt, hdrChoices, value => Markup.Escape(value), hdrTitle);

            // Update configuration
            cfg.ExecutablePath = newExecutablePath;
            cfg.DisplayName = string.IsNullOrWhiteSpace(displayName) ? null : displayName.Trim();
            cfg.IsEnabled = string.Equals(enable, "启用", StringComparison.Ordinal);
            cfg.HDREnabled = string.Equals(hdr, "自动开启 HDR", StringComparison.Ordinal);

            cfg.EntryId = string.IsNullOrWhiteSpace(cfg.EntryId) ? entryId : cfg.EntryId;
            configs[entryId] = cfg;
            await PersistAsync(configs).ConfigureAwait(false);
            _console.MarkupLine("[green]配置已更新。[/]");
            
            // Display updated configuration
            _console.WriteLine();
            _console.MarkupLine("[yellow]更新后的配置：[/]");
            _console.MarkupLine($"  DataKey: {Markup.Escape(cfg.DataKey)}");
            if (!string.IsNullOrWhiteSpace(cfg.ExecutablePath))
            {
                _console.MarkupLine($"  完整路径: {Markup.Escape(cfg.ExecutablePath)}");
            }
            if (!string.IsNullOrWhiteSpace(cfg.DisplayName))
            {
                _console.MarkupLine($"  显示名称: {Markup.Escape(cfg.DisplayName)}");
            }
        }

        private async Task RemoveGameAsync()
        {
            var configs = LoadConfigs();
            if (configs.Count == 0)
            {
                _console.MarkupLine("[italic grey]当前没有可删除的游戏。[/]");
                return;
            }

            var title = "选择要删除的游戏";
            var choices = configs.Values
                .Select(c => c.DataKey)
                .OrderBy(k => k, StringComparer.OrdinalIgnoreCase)
                .ToList();
            var prompt = new SelectionPrompt<string>
            {
                PageSize = 10
            };
            prompt.Title(title);
            prompt.AddChoices(choices);

            var selectedDataKey = PromptSelection(prompt, choices, value => Markup.Escape(value), title);
            var selected = configs.FirstOrDefault(kv => string.Equals(kv.Value.DataKey, selectedDataKey, StringComparison.OrdinalIgnoreCase));
            if (selected.Value is null || string.IsNullOrWhiteSpace(selected.Key))
            {
                _console.MarkupLine("[red]未找到对应的配置。[/]");
                return;
            }

            var confirm = PromptConfirm($"确定要删除 [bold]{Markup.Escape(selectedDataKey)}[/] 吗？");
            if (!confirm)
            {
                return;
            }

            configs.Remove(selected.Key);
            await PersistAsync(configs).ConfigureAwait(false);
            _console.MarkupLine("[yellow]已移除该游戏。[/]");
        }

        private void ShowStatistics()
        {
            var statsRule = new Rule("[yellow]游戏时长统计[/]")
            {
                Style = new Style(Color.Grey),
                Justification = Justify.Left
            };
            _console.Write(statsRule);

            var filterPrompt = new TextPrompt<string>("输入要筛选的游戏名称（留空表示全部）") { AllowEmpty = true };
            var filter = Prompt(filterPrompt);
            filter = string.IsNullOrWhiteSpace(filter) ? null : filter.Trim();

            if (!TryLoadPlaytimeData(out var items, out var source))
            {
                _console.MarkupLine("[italic grey]尚未生成任何游戏时长数据。[/]");
                WaitForMenuReturn();
                return;
            }

            var list = string.IsNullOrWhiteSpace(filter)
                ? items
                : items.Where(i => string.Equals(i.GameName, filter, StringComparison.OrdinalIgnoreCase)).ToList();

            if (list.Count == 0)
            {
                _console.MarkupLine($"[yellow]未找到与 [bold]{Markup.Escape(filter!)}[/] 匹配的记录。[/]");
                WaitForMenuReturn();
                return;
            }

            var cfg = new Dictionary<string, GameConfig>(_configProvider.Load(), StringComparer.OrdinalIgnoreCase);
            var configLookup = GameConfigLookup.Build(cfg);
            var now = DateTime.Now;
            var cutoff = now.AddDays(-14);

            var projected = list.Select(g => new
            {
                Name = ResolveDisplayName(g.GameName, configLookup),
                TotalMinutes = g.Sessions?.Sum(s => s.DurationMinutes) ?? 0,
                RecentMinutes = g.Sessions?.Where(s => s.EndTime >= cutoff).Sum(s => s.DurationMinutes) ?? 0,
                Sessions = g.Sessions?.Count ?? 0
            })
            .OrderByDescending(x => x.RecentMinutes)
            .ThenByDescending(x => x.TotalMinutes)
            .ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

            if (projected.Count == 0)
            {
                _console.MarkupLine("[italic grey]没有可展示的数据。[/]");
                WaitForMenuReturn();
                return;
            }

            var table = new Table { Border = TableBorder.Rounded };
            table.AddColumn("游戏");
            table.AddColumn("总时长");
            table.AddColumn("近两周");
            table.AddColumn("会话数");

            foreach (var entry in projected)
            {
                table.AddRow(
                    Markup.Escape(entry.Name),
                    DurationFormatter.Format(entry.TotalMinutes),
                    DurationFormatter.Format(entry.RecentMinutes),
                    entry.Sessions.ToString());
            }

            if (string.IsNullOrWhiteSpace(filter))
            {
                var totalAll = projected.Sum(p => p.TotalMinutes);
                var totalRecent = projected.Sum(p => p.RecentMinutes);
                var totalSessions = projected.Sum(p => p.Sessions);
                table.AddEmptyRow();
                table.AddRow("[bold]TOTAL[/]",
                    $"[bold]{DurationFormatter.Format(totalAll)}[/]",
                    $"[bold]{DurationFormatter.Format(totalRecent)}[/]",
                    $"[bold]{totalSessions}[/]");
            }

            _console.Write(table);
            if (!string.IsNullOrWhiteSpace(source))
            {
                _console.MarkupLine($"[grey]数据来源：{Markup.Escape(source)}[/]");
            }

            WaitForMenuReturn();
        }

        private void HandleTools()
        {
            while (true)
            {
                var title = "[bold green]工具与诊断[/]";
                var choices = Enum.GetValues<ToolAction>();
                var prompt = new SelectionPrompt<ToolAction>
                {
                    PageSize = 4
                };
                prompt.Title(title);
                prompt.AddChoices(choices);

                var choice = PromptSelection(
                    prompt,
                    choices,
                    action => action switch
                    {
                        ToolAction.ConvertConfig => "🔄  将旧版 JSON 配置转换为 YAML",
                        ToolAction.ValidateConfig => "✅  校验当前 YAML 配置",
                        ToolAction.Back => "⬅️  返回上一级",
                        _ => action.ToString()
                    },
                    title);
                switch (choice)
                {
                    case ToolAction.ConvertConfig:
                        ConvertLegacyConfig();
                        break;
                    case ToolAction.ValidateConfig:
                        ValidateCurrentConfig();
                        break;
                    case ToolAction.Back:
                        return;
                }
            }
        }

        private void ConvertLegacyConfig()
        {
            try
            {
                string dir = AppDataPath.GetGameHelperDirectory();
                string jsonPath = Path.Combine(dir, "config.json");
                string ymlPath = AppDataPath.GetConfigPath();

                if (!File.Exists(jsonPath))
                {
                    _console.MarkupLine($"[yellow]未在 {Markup.Escape(jsonPath)} 找到旧版 JSON 配置。[/]");
                    return;
                }

                _console.Status().Start("转换配置中...", ctx =>
                {
                    var jsonProvider = new JsonConfigProvider(jsonPath);
                    var data = jsonProvider.Load();
                    ctx.Status("写入 YAML...");
                    var yamlProvider = new YamlConfigProvider(ymlPath);
                    yamlProvider.Save(data);
                });

                _console.MarkupLine($"[green]转换完成[/]，YAML 已写入 {Markup.Escape(ymlPath)}。");
            }
            catch (Exception ex)
            {
                _console.MarkupLine($"[red]转换失败：{Markup.Escape(ex.Message)}[/]");
            }
        }

        private void ValidateCurrentConfig()
        {
            try
            {
                var provider = new YamlConfigProvider();
                string path = provider.ConfigPath;
                var result = YamlConfigValidator.Validate(path);

                var table = new Table { Border = TableBorder.Rounded };
                table.AddColumn("指标");
                table.AddColumn("数值");
                table.AddRow("配置路径", Markup.Escape(path));
                table.AddRow("游戏数量", result.GameCount.ToString());
                table.AddRow("重复条目", result.DuplicateCount.ToString());
                table.AddRow("状态", result.IsValid ? "[green]通过[/]" : "[red]存在错误[/]");

            
                _console.Write(table);

                if (result.Warnings.Count > 0)
                {
                    _console.MarkupLine("[yellow]警告：[/]");
                    foreach (var warning in result.Warnings)
                    {
                        _console.MarkupLine($"  • {Markup.Escape(warning)}");
                    }
                }

                if (result.Errors.Count > 0)
                {
                    _console.MarkupLine("[red]错误：[/]");
                    foreach (var error in result.Errors)
                    {
                        _console.MarkupLine($"  • {Markup.Escape(error)}");
                    }
                }
            }
            catch (Exception ex)
            {
                _console.MarkupLine($"[red]验证失败：{Markup.Escape(ex.Message)}[/]");
            }
        }

        private Dictionary<string, GameConfig> LoadConfigs()
        {
            return new Dictionary<string, GameConfig>(_configProvider.Load(), StringComparer.OrdinalIgnoreCase);
        }

        private async Task PersistAsync(Dictionary<string, GameConfig> configs)
        {
            await Task.Run(() => _configProvider.Save(configs)).ConfigureAwait(false);
        }

        private bool TryLoadPlaytimeData(out List<GameItem> items, out string source)
        {
            string dir = AppDataPath.GetGameHelperDirectory();
            string csvFile = AppDataPath.GetPlaytimeCsvPath();
            string jsonFile = AppDataPath.GetPlaytimeJsonPath();

            if (File.Exists(csvFile))
            {
                items = PlaytimeDataReader.ReadFromCsv(csvFile);
                source = csvFile;
                return true;
            }

            if (File.Exists(jsonFile))
            {
                items = PlaytimeDataReader.ReadFromJson(jsonFile);
                source = jsonFile;
                return true;
            }

            items = new List<GameItem>();
            source = string.Empty;
            return false;
        }

        private string GetConfigPathDescription()
        {
            if (_configProvider is IConfigPathProvider pathProvider)
            {
                return pathProvider.ConfigPath;
            }
            return "默认 AppData 目录";
        }

        private string GetMonitorModeDescription()
        {
            if (_arguments.MonitorDryRun)
            {
                return "Dry-run（仅演练）";
            }

            if (!string.IsNullOrWhiteSpace(_arguments.MonitorType))
            {
                return $"{_arguments.MonitorType!.ToUpperInvariant()}（命令行指定）";
            }

            try
            {
                var appConfig = _appConfigProvider.LoadAppConfig();
                if (appConfig.ProcessMonitorType.HasValue)
                {
                    return $"{appConfig.ProcessMonitorType.Value}（配置文件）";
                }
            }
            catch
            {
                // Ignore configuration load failures for display purposes.
            }

            return "WMI（默认）";
        }

        private static string GetVersionDescription()
        {
            try
            {
                var assembly = Assembly.GetEntryAssembly() ?? typeof(InteractiveShell).Assembly;
                var informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
                if (!string.IsNullOrWhiteSpace(informational))
                {
                    var plusIndex = informational.IndexOf('+');
                    return plusIndex > 0 ? informational[..plusIndex] : informational;
                }

                return assembly.GetName().Version?.ToString() ?? "unknown";
            }
            catch
            {
                return "unknown";
            }
        }

        private static string GetBuildTimeDescription()
        {
            try
            {
                string? path = Assembly.GetEntryAssembly()?.Location;
                if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                {
                    try
                    {
                        path = System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName;
                    }
                    catch
                    {
                        path = null;
                    }
                }

                if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                {
                    return "unknown";
                }

                return File.GetLastWriteTime(path).ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
            }
            catch
            {
                return "unknown";
            }
        }

        private T PromptSelection<T>(SelectionPrompt<T> prompt, IReadOnlyList<T> choices, Func<T, string> labelFactory, string? displayTitle)
            where T : notnull
        {
            if (_script != null && _script.TryDequeue(out T scriptedValue))
            {
                return scriptedValue;
            }

            var entries = choices
                .Select((choice, index) => new NumberedChoice<T>(index + 1, choice, labelFactory(choice)))
                .ToList();

            var lookup = entries.ToDictionary(entry => entry.Value, entry => entry, EqualityComparer<T>.Default);

            prompt.UseConverter(value =>
            {
                return lookup.TryGetValue(value, out var entry)
                    ? FormatNumberedLabel(entry)
                    : labelFactory(value);
            });

            RenderNumberedChoices(displayTitle, entries);

            if (_script is null && TrySelectByNumber(entries, out var directSelection))
            {
                return directSelection;
            }

            while (true)
            {
                var inputPrompt = new TextPrompt<string>("请输入选项序号（直接输入数字或按 Enter 使用方向键）")
                    .AllowEmpty()
                    .DefaultValue(string.Empty);

                var input = Prompt(inputPrompt);

                if (string.IsNullOrWhiteSpace(input))
                {
                    _console.WriteLine();
                    return _console.Prompt(prompt);
                }

                if (int.TryParse(input, NumberStyles.Integer, CultureInfo.InvariantCulture, out var index)
                    && index >= 1 && index <= entries.Count)
                {
                    _console.WriteLine();
                    return entries[index - 1].Value;
                }

                _console.MarkupLine("[red]无效的序号，请重新输入。[/]");
            }
        }

        private void RenderNumberedChoices<T>(string? title, List<NumberedChoice<T>> entries)
            where T : notnull
        {
            if (!string.IsNullOrWhiteSpace(title))
            {
                _console.MarkupLine(title!);
            }

            if (entries.Count > 0)
            {
                var grid = new Grid();
                grid.AddColumn(new GridColumn().NoWrap().PadLeft(0).PadRight(1));
                grid.AddColumn(new GridColumn().PadLeft(0));

                foreach (var entry in entries)
                {
                    grid.AddRow(new Markup($"[grey]{entry.Index}.[/]"), new Markup(entry.Label));
                }

                _console.Write(grid);
                _console.WriteLine();
                _console.MarkupLine("[grey]直接输入序号即可选择；按 Enter 使用方向键。[/]");
            }

            _console.WriteLine();
        }

        private static string FormatNumberedLabel<T>(NumberedChoice<T> entry)
            where T : notnull
        {
            return $"[grey]{entry.Index}.[/] {entry.Label}";
        }

        private sealed record NumberedChoice<T>(int Index, T Value, string Label)
            where T : notnull;

        private bool TrySelectByNumber<T>(List<NumberedChoice<T>> entries, out T value)
            where T : notnull
        {
            value = default!;

            if (entries.Count == 0 || Console.IsInputRedirected || _script is not null)
            {
                return false;
            }

            var buffer = new StringBuilder();
            DateTime? deadline = null;

            while (true)
            {
                if (buffer.Length > 0 && deadline.HasValue && DateTime.UtcNow >= deadline.Value)
                {
                    if (TryResolveSelection(entries, buffer.ToString(), out value))
                    {
                        return true;
                    }

                    buffer.Clear();
                    deadline = null;
                    _console.MarkupLine("[red]无效的序号，请重新输入。[/]");
                    continue;
                }

                ConsoleKeyInfo keyInfo;
                if (buffer.Length == 0)
                {
                    if (!TryReadKey(out keyInfo))
                    {
                        return false;
                    }
                }
                else
                {
                    if (!TryReadKeyIfAvailable(out keyInfo, out var pollingError))
                    {
                        if (pollingError)
                        {
                            return false;
                        }

                        Thread.Sleep(DirectNumberPollingMilliseconds);
                        continue;
                    }
                }

                if (keyInfo.Key == ConsoleKey.Enter)
                {
                    if (buffer.Length == 0)
                    {
                        _console.WriteLine();
                        return false;
                    }

                    if (TryResolveSelection(entries, buffer.ToString(), out value))
                    {
                        return true;
                    }

                    buffer.Clear();
                    deadline = null;
                    _console.MarkupLine("[red]无效的序号，请重新输入。[/]");
                    continue;
                }

                if (keyInfo.Key == ConsoleKey.Backspace)
                {
                    if (buffer.Length > 0)
                    {
                        buffer.Remove(buffer.Length - 1, 1);
                        deadline = buffer.Length == 0
                            ? null
                            : CalculateDeadline(buffer.ToString(), entries.Count);
                    }

                    continue;
                }

                if (!char.IsDigit(keyInfo.KeyChar))
                {
                    if (buffer.Length == 0)
                    {
                        continue;
                    }

                    buffer.Clear();
                    deadline = null;
                    _console.MarkupLine("[red]无效的序号，请重新输入。[/]");
                    continue;
                }

                if (buffer.Length == 0 && keyInfo.KeyChar == '0')
                {
                    _console.MarkupLine("[red]无效的序号，请重新输入。[/]");
                    continue;
                }

                buffer.Append(keyInfo.KeyChar);

                if (!int.TryParse(buffer.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var index)
                    || index < 1 || index > entries.Count)
                {
                    buffer.Clear();
                    deadline = null;
                    _console.MarkupLine("[red]无效的序号，请重新输入。[/]");
                    continue;
                }

                if (HasContinuationPotential(index, entries.Count))
                {
                    deadline = DateTime.UtcNow.Add(NumericSelectionIdleTimeout);
                    continue;
                }

                if (TryResolveSelection(entries, buffer.ToString(), out value))
                {
                    return true;
                }

                buffer.Clear();
                deadline = null;
                _console.MarkupLine("[red]无效的序号，请重新输入。[/]");
            }
        }

        private static bool TryReadKey(out ConsoleKeyInfo keyInfo)
        {
            keyInfo = default;

            try
            {
                keyInfo = Console.ReadKey(intercept: true);
                return true;
            }
            catch (InvalidOperationException)
            {
            }
            catch (IOException)
            {
            }
            catch (PlatformNotSupportedException)
            {
            }

            return false;
        }

        private static bool TryReadKeyIfAvailable(out ConsoleKeyInfo keyInfo, out bool encounteredError)
        {
            keyInfo = default;
            encounteredError = false;

            try
            {
                if (!Console.KeyAvailable)
                {
                    return false;
                }

                keyInfo = Console.ReadKey(intercept: true);
                return true;
            }
            catch (InvalidOperationException)
            {
            }
            catch (IOException)
            {
            }
            catch (PlatformNotSupportedException)
            {
            }

            encounteredError = true;
            return false;
        }

        private bool TryResolveSelection<T>(List<NumberedChoice<T>> entries, string digits, out T value)
            where T : notnull
        {
            if (int.TryParse(digits, NumberStyles.Integer, CultureInfo.InvariantCulture, out var index)
                && index >= 1 && index <= entries.Count)
            {
                _console.WriteLine();
                value = entries[index - 1].Value;
                return true;
            }

            value = default!;
            return false;
        }

        private static DateTime? CalculateDeadline(string digits, int count)
        {
            if (!int.TryParse(digits, NumberStyles.Integer, CultureInfo.InvariantCulture, out var index))
            {
                return null;
            }

            return HasContinuationPotential(index, count)
                ? DateTime.UtcNow.Add(NumericSelectionIdleTimeout)
                : null;
        }

        private static bool HasContinuationPotential(int current, int count)
        {
            var candidate = (long)current * 10;
            return candidate <= count;
        }

        private T Prompt<T>(IPrompt<T> prompt)
        {
            if (_script != null && _script.TryDequeue(out T scriptedValue))
            {
                return scriptedValue;
            }

            return _console.Prompt(prompt);
        }

        private bool PromptConfirm(string message, bool defaultValue = false)
        {
            if (_script != null && _script.TryDequeue(out bool scriptedValue))
            {
                return scriptedValue;
            }

            return _console.Confirm(message, defaultValue);
        }

        private void WaitForMenuReturn()
        {
            _console.WriteLine();
            var prompt = new TextPrompt<string>("[grey]按下 Enter 返回主菜单[/]")
                .AllowEmpty()
                .DefaultValue(string.Empty);
            Prompt(prompt);
        }

        private void RenderMonitorHistory(SessionSnapshot snapshot)
        {
            if (snapshot.Records.Count == 0)
            {
                var placeholder = new Panel(new Markup("[italic grey]最近暂无监控记录。完成一次会话后，会在此展示新增摘要。[/]"))
                {
                    Header = new PanelHeader("历史记录预览"),
                    Border = BoxBorder.Rounded
                };
                _console.Write(placeholder);
                return;
            }

            var table = new Table { Border = TableBorder.Rounded };
            table.AddColumn("游戏");
            table.AddColumn("结束时间");
            table.AddColumn("时长");

            foreach (var record in snapshot.Records
                .OrderByDescending(r => r.End)
                .Take(3))
            {
                table.AddRow(
                    Markup.Escape(record.DisplayName),
                    FormatTimestamp(record.End),
                    DurationFormatter.Format(record.DurationMinutes));
            }

            table.Caption("最近 3 条记录");

            _console.Write(new Panel(table)
            {
                Header = new PanelHeader("历史记录预览"),
                Border = BoxBorder.Rounded
            });
        }

        private void RenderSessionSummary(SessionSnapshot before, SessionSnapshot after)
        {
            var newSessions = after.Records
                .Where(record => !before.Keys.Contains(record.Key))
                .OrderBy(record => record.Start)
                .ToList();

            if (newSessions.Count == 0)
            {
                _console.MarkupLine("[italic grey]本次会话没有产生新的游玩记录。[/]");
                return;
            }

            var table = new Table { Border = TableBorder.Rounded };
            table.AddColumn("游戏");
            table.AddColumn("开始");
            table.AddColumn("结束");
            table.AddColumn("本次时长");

            foreach (var record in newSessions)
            {
                table.AddRow(
                    Markup.Escape(record.DisplayName),
                    FormatTimestamp(record.Start),
                    FormatTimestamp(record.End),
                    DurationFormatter.Format(record.DurationMinutes));
            }

            var totalMinutes = newSessions.Sum(r => r.DurationMinutes);
            table.AddEmptyRow();
            table.AddRow("[bold]TOTAL[/]", string.Empty, string.Empty, $"[bold]{DurationFormatter.Format(totalMinutes)}[/]");

            _console.Write(table);

            var aggregated = newSessions
                .GroupBy(r => r.DisplayName)
                .Select(g => new { g.Key, Minutes = g.Sum(r => r.DurationMinutes), Count = g.Count() })
                .OrderByDescending(x => x.Minutes)
                .ToList();

            var summaryText = string.Join("，", aggregated.Select(g => $"{g.Key} {DurationFormatter.Format(g.Minutes)}（{g.Count} 次）"));
            _console.MarkupLine($"[grey]本次共计 {newSessions.Count} 次游戏结束：{summaryText}[/]");

            if (!string.IsNullOrWhiteSpace(after.Source))
            {
                _console.MarkupLine($"[grey]数据已写入：{Markup.Escape(after.Source)}[/]");
            }
        }

        private SessionSnapshot CaptureSessionSnapshot()
        {
            if (!TryLoadPlaytimeData(out var items, out var source))
            {
                return new SessionSnapshot(new HashSet<SessionKey>(), new List<SessionRecord>(), source);
            }

            var configs = new Dictionary<string, GameConfig>(_configProvider.Load(), StringComparer.OrdinalIgnoreCase);
            var configLookup = GameConfigLookup.Build(configs);
            var keys = new HashSet<SessionKey>();
            var records = new List<SessionRecord>();

            foreach (var item in items)
            {
                var displayName = ResolveDisplayName(item.GameName, configLookup);

                foreach (var session in item.Sessions)
                {
                    var record = new SessionRecord(item.GameName, displayName, session.StartTime, session.EndTime, session.DurationMinutes);
                    keys.Add(record.Key);
                    records.Add(record);
                }
            }

            return new SessionSnapshot(keys, records, source);
        }

        private static string ResolveDisplayName(string key, GameConfigLookup lookup)
        {
            var cfg = lookup.Resolve(key);
            return !string.IsNullOrWhiteSpace(cfg?.DisplayName) ? cfg.DisplayName! : key;
        }

        private static string FormatTimestamp(DateTime timestamp)
        {
            if (timestamp.Kind == DateTimeKind.Utc)
            {
                timestamp = timestamp.ToLocalTime();
            }

            return timestamp.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
        }

        private sealed record SessionSnapshot(HashSet<SessionKey> Keys, List<SessionRecord> Records, string Source);

        private sealed class SessionRecord
        {
            public SessionRecord(string gameName, string displayName, DateTime start, DateTime end, long minutes)
            {
                Key = new SessionKey(gameName, start, end, minutes);
                DisplayName = displayName;
            }

            public SessionKey Key { get; }

            public string DisplayName { get; }

            public DateTime Start => Key.Start;

            public DateTime End => Key.End;

            public long DurationMinutes => Key.DurationMinutes;
        }

        private readonly record struct SessionKey(string Game, DateTime Start, DateTime End, long DurationMinutes);

    }
}
