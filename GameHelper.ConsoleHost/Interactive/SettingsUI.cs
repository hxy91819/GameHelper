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
        private readonly IAppConfigProvider _appConfigProvider;
        private readonly IAutoStartManager _autoStartManager;

        public SettingsUI(IAnsiConsole console, PromptUI promptUI, IAppConfigProvider appConfigProvider, IAutoStartManager autoStartManager)
        {
            _console = console ?? throw new ArgumentNullException(nameof(console));
            _promptUI = promptUI ?? throw new ArgumentNullException(nameof(promptUI));
            _appConfigProvider = appConfigProvider ?? throw new ArgumentNullException(nameof(appConfigProvider));
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

                var title = "[bold green]全局设置[/]";
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

            var selection = _promptUI.PromptSelection(prompt, options, value => Markup.Escape(value), title);
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

            var selection = _promptUI.PromptSelection(prompt, options, value => Markup.Escape(value), title);
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

        internal bool? TryReadSystemAutoStartState()
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
    }

    internal enum SettingsAction
    {
        ToggleMonitorAutoStart,
        ToggleLaunchOnStartup,
        Back
    }
}
