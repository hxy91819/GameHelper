using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using GameHelper.ConsoleHost.Utilities;
using GameHelper.Core.Abstractions;
using GameHelper.Core.Models;
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
        private readonly IGameConfiguration _gameConfiguration;
        private readonly bool _autoStartMonitor;

        public InteractiveShell(IHost host, ParsedArguments arguments, IAnsiConsole? console = null, InteractiveScript? script = null, Func<IHost, CancellationToken, Task>? monitorLoop = null)
        {
            _arguments = arguments ?? throw new ArgumentNullException(nameof(arguments));
            var modules = InteractiveShellModules.Create(host, _arguments, console, script, monitorLoop);
            _console = modules.Console;
            _promptUI = modules.PromptUI;
            _gameConfiguration = modules.GameConfiguration;
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
                Console.Title = "GameHelper Interactive Console";
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
                    _console.MarkupLine("[grey]Auto-start is enabled in config; entering live monitor directly.[/]");
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
                        _console.MarkupLine("[grey]Goodbye, happy gaming![/]");
                        return;
                }
            }
        }

        private bool DetermineAutoStartPreference()
        {
            try
            {
                var appConfig = _gameConfiguration.Read();
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
            infoTable.AddColumn(new TableColumn("Current context").Centered());
            infoTable.AddColumn(new TableColumn("Details"));
            infoTable.AddRow("Config file", GetConfigPathDescription());
            infoTable.AddRow("Log level", _arguments.EnableDebug ? "Debug (enabled via command line)" : "Information");
            infoTable.AddRow("Monitor mode", GetMonitorModeDescription());
            infoTable.AddRow("Version", BuildInfoHelper.GetVersionDescription());
            infoTable.AddRow("Build date", BuildInfoHelper.GetBuildTimeDescription());
            infoTable.AddRow("Commit", BuildInfoHelper.GetCommitId());
            infoTable.Caption("Type a number or use arrow keys, then press Enter");
            _console.Write(infoTable);

            _console.WriteLine();
        }

        private MainMenuAction PromptMainMenu()
        {
            var title = "[bold green]Select an action:[/]";
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
                        MainMenuAction.Monitor => "🚀  Start live monitor",
                        MainMenuAction.Configuration => "🛠   Manage game config",
                        MainMenuAction.Settings => "⚙️   Global settings",
                        MainMenuAction.Statistics => "📊  View playtime stats",
                        MainMenuAction.Tools => "🧰  Tools & diagnostics",
                        MainMenuAction.Exit => "⬅️   Exit",
                    _ => action.ToString()
                },
                title);
        }

        private string GetConfigPathDescription()
        {
            if (_gameConfiguration is IConfigPathProvider pathProvider)
            {
                return pathProvider.ConfigPath;
            }
            return "Default AppData directory";
        }

        private string GetMonitorModeDescription()
        {
            ProcessMonitorType? configuredMonitorType = null;
            try
            {
                if (!_arguments.MonitorDryRun)
                {
                    configuredMonitorType = _gameConfiguration.Read().ProcessMonitorType;
                }
            }
            catch
            {
                // Ignore configuration load failures for display purposes.
            }

            return MonitorModeSelection.Resolve(_arguments, configuredMonitorType).GetDisplayText();
        }
    }
}
