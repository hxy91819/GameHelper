using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using GameHelper.Core.Abstractions;
using GameHelper.Core.Models;
using Spectre.Console;

namespace GameHelper.ConsoleHost.Interactive
{
    /// <summary>
    /// 全局设置管理：监控自动启动、开机自启动。
    /// </summary>
    internal sealed class SettingsUI
    {
        private readonly IAnsiConsole _console;
        private readonly PromptUI _promptUI;
        private readonly IGameConfiguration _gameConfiguration;
        private readonly IAutoStartManager _autoStartManager;

        public SettingsUI(IAnsiConsole console, PromptUI promptUI, IGameConfiguration gameConfiguration, IAutoStartManager autoStartManager)
        {
            _console = console ?? throw new ArgumentNullException(nameof(console));
            _promptUI = promptUI ?? throw new ArgumentNullException(nameof(promptUI));
            _gameConfiguration = gameConfiguration ?? throw new ArgumentNullException(nameof(gameConfiguration));
            _autoStartManager = autoStartManager ?? throw new ArgumentNullException(nameof(autoStartManager));
        }

        public async Task HandleSettingsAsync()
        {
            while (true)
            {
                var actions = new List<SettingsAction> { SettingsAction.ToggleMonitorAutoStart };
                if (_autoStartManager.IsSupported)
                {
                    actions.Add(SettingsAction.ToggleLaunchOnStartup);
                }
                actions.Add(SettingsAction.Back);

                var title = "[bold green]Global settings[/]";
                var prompt = new SelectionPrompt<SettingsAction>
                {
                    PageSize = actions.Count
                };
                prompt.Title(title);
                prompt.AddChoices(actions);

                var selection = _promptUI.PromptSelection(
                    prompt,
                    actions,
                    action => action switch
                    {
                        SettingsAction.ToggleMonitorAutoStart => "⚡️  Auto-enter live monitor on startup",
                        SettingsAction.ToggleLaunchOnStartup when _autoStartManager.IsSupported => "🖥️  Configure auto-start at login",
                        SettingsAction.ToggleLaunchOnStartup => "🖥️  Auto-start at login (not supported in this environment)",
                        SettingsAction.Back => "⬅️  Back",
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
                            _console.MarkupLine("[yellow]Auto-start at login is not supported on this platform.[/]");
                        }
                        break;

                    case SettingsAction.Back:
                        return;
                }
            }
        }

        private async Task ConfigureMonitorAutoStartAsync()
        {
            AppConfig appConfig;
            try
            {
                appConfig = _gameConfiguration.Read();
            }
            catch (Exception ex)
            {
                _console.MarkupLine($"[red]Failed to load global config: {Markup.Escape(ex.Message)}[/]");
                return;
            }

            var current = appConfig.AutoStartInteractiveMonitor;
            var enableOption = current ? "Keep auto-start" : "Enable auto-start";
            var disableOption = current ? "Switch to manual start" : "Keep manual start";
            var options = new[] { enableOption, disableOption };

            var title = "Automatically enter live monitor after startup?";
            var prompt = new SelectionPrompt<string>();
            prompt.Title(title);
            prompt.AddChoices(options);

            var selection = _promptUI.PromptSelection(prompt, options, value => Markup.Escape(value), title);
            var newValue = string.Equals(selection, enableOption, StringComparison.Ordinal);

            if (newValue == current)
            {
                _console.MarkupLine("[grey]Setting left unchanged.[/]");
                return;
            }

            appConfig.AutoStartInteractiveMonitor = newValue;

            try
            {
                await Task.Run(() => _gameConfiguration.Change(current => CopySettings(appConfig, current))).ConfigureAwait(false);
                var resultMessage = newValue
                    ? "[green]Updated: live monitor will start automatically on launch.[/]"
                    : "[green]Updated: monitor must be selected manually on launch.[/]";
                _console.MarkupLine(resultMessage);
            }
            catch (Exception ex)
            {
                _console.MarkupLine($"[red]Failed to save config: {Markup.Escape(ex.Message)}[/]");
            }
        }

        private async Task ConfigureSystemAutoStartAsync()
        {
            if (!_autoStartManager.IsSupported)
            {
                _console.MarkupLine("[yellow]Auto-start at login is not supported on this platform.[/]");
                return;
            }

            AppConfig appConfig;
            try
            {
                appConfig = _gameConfiguration.Read();
            }
            catch (Exception ex)
            {
                _console.MarkupLine($"[red]Failed to load global config: {Markup.Escape(ex.Message)}[/]");
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
                        ? "[green]System is currently enabled.[/]"
                        : "[yellow]System is currently not enabled.[/]";

                    if (systemState.Value != currentConfigValue)
                    {
                        status += "[yellow](mismatch with config)[/]";
                    }

                    _console.MarkupLine(status);
                }
            }

            var enableOption = currentConfigValue ? "Keep auto-start at login" : "Enable auto-start at login";
            var disableOption = currentConfigValue ? "Disable auto-start at login" : "Keep disabled";
            var options = new[] { enableOption, disableOption };

            var title = "Start GameHelper automatically at login?";
            var prompt = new SelectionPrompt<string>();
            prompt.Title(title);
            prompt.AddChoices(options);

            var selection = _promptUI.PromptSelection(prompt, options, value => Markup.Escape(value), title);
            var newValue = string.Equals(selection, enableOption, StringComparison.Ordinal);

            var needsSystemUpdate = !systemState.HasValue || systemState.Value != newValue;
            var needsConfigUpdate = newValue != currentConfigValue;

            if (!needsSystemUpdate && !needsConfigUpdate)
            {
                _console.MarkupLine("[grey]Setting left unchanged.[/]");
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
                    _console.MarkupLine($"[red]Failed to update system auto-start setting: {Markup.Escape(ex.Message)}[/]");
                    return;
                }
            }

            if (needsConfigUpdate)
            {
                appConfig.LaunchOnSystemStartup = newValue;

                try
                {
                    await Task.Run(() => _gameConfiguration.Change(current => CopySettings(appConfig, current))).ConfigureAwait(false);
                    var resultMessage = newValue
                        ? "[green]Updated: GameHelper will start automatically at login.[/]"
                        : "[green]Updated: GameHelper will not start automatically at login.[/]";
                    _console.MarkupLine(resultMessage);
                }
                catch (Exception ex)
                {
                    _console.MarkupLine($"[red]Failed to save config: {Markup.Escape(ex.Message)}[/]");
                }
            }
            else
            {
                var ensureMessage = newValue
                    ? "[green]System auto-start is now enabled.[/]"
                    : "[green]System auto-start is now disabled.[/]";
                _console.MarkupLine(ensureMessage);
            }
        }

        internal bool? TryReadSystemAutoStartState()
        {
            try
            {
                return _autoStartManager.IsEnabled();
            }
            catch (Exception ex)
            {
                _console.MarkupLine($"[yellow]Failed to read system auto-start state: {Markup.Escape(ex.Message)}[/]");
                return null;
            }
        }

        private static void CopySettings(AppConfig source, AppConfig destination)
        {
            destination.ProcessMonitorType = source.ProcessMonitorType;
            destination.AutoStartInteractiveMonitor = source.AutoStartInteractiveMonitor;
            destination.LaunchOnSystemStartup = source.LaunchOnSystemStartup;
        }
    }

    internal enum SettingsAction
    {
        ToggleMonitorAutoStart,
        ToggleLaunchOnStartup,
        Back
    }
}
