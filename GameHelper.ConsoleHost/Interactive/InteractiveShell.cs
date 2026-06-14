using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using GameHelper.ConsoleHost.Models;
using GameHelper.ConsoleHost.Utilities;
using GameHelper.Core.Abstractions;
using Microsoft.Extensions.Hosting;
using Spectre.Console;

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


        private readonly StatisticsUI _statisticsUI;
        private readonly ToolsUI _toolsUI;
        private readonly SettingsUI _settingsUI;
        private readonly GameCatalogUI _catalogUI;
        private readonly MonitorUI _monitorUI;
        private readonly ParsedArguments _arguments;
        private readonly IAnsiConsole _console;
        private readonly PromptUI _promptUI;
        private readonly IConfigProvider _configProvider;
        private readonly IAppConfigProvider _appConfigProvider;
        private readonly bool _autoStartMonitor;

        public InteractiveShell(IHost host, ParsedArguments arguments, IAnsiConsole? console = null, InteractiveScript? script = null, Func<IHost, CancellationToken, Task>? monitorLoop = null)
        {
            _arguments = arguments ?? throw new ArgumentNullException(nameof(arguments));
            var modules = InteractiveShellModules.Create(host, _arguments, console, script, monitorLoop);
            _console = modules.Console;
            _promptUI = modules.PromptUI;
            _configProvider = modules.ConfigProvider;
            _appConfigProvider = modules.AppConfigProvider;
            _monitorUI = modules.MonitorUI;
            _catalogUI = modules.CatalogUI;
            _settingsUI = modules.SettingsUI;
            _statisticsUI = modules.StatisticsUI;
            _toolsUI = modules.ToolsUI;
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
                        await _monitorUI.LaunchMonitorAsync().ConfigureAwait(false);
                        break;

                    case MainMenuAction.Configuration:
                        await _catalogUI.HandleConfigurationAsync().ConfigureAwait(false);
                        break;

                    case MainMenuAction.Settings:
                        await _settingsUI.HandleSettingsAsync().ConfigureAwait(false);
                        break;

                    case MainMenuAction.Statistics:
                        _statisticsUI.ShowStatistics();
                        break;

                    case MainMenuAction.Tools:
                        _toolsUI.HandleTools();
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
            var infoTable = new Table { Border = TableBorder.Rounded };
            infoTable.AddColumn(new TableColumn("当前上下文").Centered());
            infoTable.AddColumn(new TableColumn("详情"));
            infoTable.AddRow("配置文件", GetConfigPathDescription());
            infoTable.AddRow("日志级别", _arguments.EnableDebug ? "Debug（命令行启用）" : "Information");
            infoTable.AddRow("监控模式", GetMonitorModeDescription());
            infoTable.AddRow("版本", BuildInfoHelper.GetVersionDescription());
            infoTable.AddRow("构建日期", BuildInfoHelper.GetBuildTimeDescription());
            infoTable.AddRow("Commit", BuildInfoHelper.GetCommitId());
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

            return _promptUI.PromptSelection(
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
    }
}
