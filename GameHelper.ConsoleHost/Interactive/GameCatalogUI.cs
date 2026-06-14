using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using GameHelper.ConsoleHost.Utilities;
using GameHelper.Core.Abstractions;
using GameHelper.Core.Models;
using Spectre.Console;
using ConsoleValidationResult = Spectre.Console.ValidationResult;

namespace GameHelper.ConsoleHost.Interactive
{
    /// <summary>
    /// Handles game catalog management interactions (view, add, edit, remove).
    /// </summary>
    public sealed class GameCatalogUI
    {
        private enum ConfigAction
        {
            View,
            Add,
            Edit,
            Remove,
            Back
        }

        private readonly IAnsiConsole _console;
        private readonly PromptUI _promptUI;
        private readonly IAppConfigProvider _appConfigProvider;
        private readonly IAutoStartManager _autoStartManager;
        private readonly IGameCatalogService _gameCatalogService;

        public GameCatalogUI(
            IAnsiConsole console,
            PromptUI promptUI,
            IAppConfigProvider appConfigProvider,
            IAutoStartManager autoStartManager,
            IGameCatalogService gameCatalogService)
        {
            _console = console ?? throw new ArgumentNullException(nameof(console));
            _promptUI = promptUI ?? throw new ArgumentNullException(nameof(promptUI));
            _appConfigProvider = appConfigProvider ?? throw new ArgumentNullException(nameof(appConfigProvider));
            _autoStartManager = autoStartManager ?? throw new ArgumentNullException(nameof(autoStartManager));
            _gameCatalogService = gameCatalogService ?? throw new ArgumentNullException(nameof(gameCatalogService));
        }

        public async Task HandleConfigurationAsync()
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

                var selection = _promptUI.PromptSelection(
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

        private void RenderConfigTable()
        {
            var configs = _gameCatalogService.GetAll()
                .ToList();
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

            foreach (var cfg in configs.OrderBy(e => e.DataKey, StringComparer.OrdinalIgnoreCase))
            {
                var pathDisplay = "-";
                if (!string.IsNullOrWhiteSpace(cfg.ExecutablePath))
                {
                    pathDisplay = cfg.ExecutablePath.Length > 30
                        ? "..." + cfg.ExecutablePath.Substring(cfg.ExecutablePath.Length - 27)
                        : cfg.ExecutablePath;
                }

                table.AddRow(
                    Markup.Escape(cfg.DataKey),
                    Markup.Escape(cfg.ExecutableName ?? "-"),
                    string.IsNullOrWhiteSpace(cfg.DisplayName) ? "-" : Markup.Escape(cfg.DisplayName!),
                    Markup.Escape(pathDisplay),
                    cfg.IsEnabled ? "[green]启用[/]" : "[red]禁用[/]",
                    cfg.HdrEnabled ? "[green]自动开启[/]" : "[yellow]不自动开启[/]");
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

        private Task AddGameAsync()
        {
            // Prompt for input - can be file path or executable name
            var inputPrompt = new TextPrompt<string>(
                "请输入游戏的可执行文件名或拖放 EXE/LNK 文件\n" +
                "(例如 [green]game.exe[/] 或完整路径 [green]C:\\Games\\game.exe[/])")
                .Validate(input => string.IsNullOrWhiteSpace(input)
                    ? ConsoleValidationResult.Error("输入不能为空。")
                    : ConsoleValidationResult.Success());

            var input = _promptUI.Prompt(inputPrompt);
            input = input.Trim().Trim('"'); // Remove quotes if dragged

            string? exePath = null;
            string executableName;
            string? productName = null;
            string dataKeyIdentity;

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
                        return Task.CompletedTask;
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
                    return Task.CompletedTask;
                }

                // Extract metadata
                (productName, _) = GameMetadataExtractor.ExtractMetadata(exePath);
                executableName = Path.GetFileName(exePath);
                dataKeyIdentity = exePath;

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
                dataKeyIdentity = input;
            }

            // Check for existing config by path first, then by unique executable name fallback.
            var existingConfig = _gameCatalogService.FindExistingForAdd(executableName, exePath);
            var existingDataKey = existingConfig?.DataKey;
            var suggestedDataKey = existingDataKey ?? _gameCatalogService.SuggestDataKey(dataKeyIdentity, productName);

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
                    if (!_gameCatalogService.IsDataKeyAvailable(key, existingDataKey))
                    {
                        return ConsoleValidationResult.Error($"DataKey '{key}' 已被其他游戏使用。");
                    }
                    return ConsoleValidationResult.Success();
                });

            var dataKey = _promptUI.Prompt(dataKeyPrompt);
            if (string.IsNullOrWhiteSpace(dataKey))
            {
                dataKey = suggestedDataKey;
            }

            // Prompt for DisplayName
            var defaultDisplayName = existingConfig != null && !string.IsNullOrWhiteSpace(existingConfig.DisplayName)
                ? existingConfig.DisplayName!
                : productName ?? string.Empty;

            var displayNamePrompt = new TextPrompt<string>("输入显示名称（可选，直接回车跳过）")
                .AllowEmpty()
                .DefaultValue(defaultDisplayName);
            var displayName = _promptUI.Prompt(displayNamePrompt);

            // Prompt for automation enable
            var enableTitle = "是否启用自动化？";
            var enableChoices = existingConfig?.IsEnabled == false
                ? new[] { "禁用", "启用" }
                : new[] { "启用", "禁用" };
            var enablePrompt = new SelectionPrompt<string>();
            enablePrompt.Title(enableTitle);
            enablePrompt.AddChoices(enableChoices);
            var enable = _promptUI.PromptSelection(enablePrompt, enableChoices, value => Markup.Escape(value), enableTitle);

            // Prompt for HDR
            var hdrTitle = "在游戏运行时如何控制 HDR？";
            var defaultHdrEnabled = existingConfig?.HdrEnabled ?? false;
            var hdrChoices = defaultHdrEnabled
                ? new[] { "自动开启 HDR", "不自动开启" }
                : new[] { "不自动开启", "自动开启 HDR" };
            var hdrPrompt = new SelectionPrompt<string>();
            hdrPrompt.Title(hdrTitle);
            hdrPrompt.AddChoices(hdrChoices);
            var hdr = _promptUI.PromptSelection(hdrPrompt, hdrChoices, value => Markup.Escape(value), hdrTitle);

            var saved = _gameCatalogService.Save(new GameEntryUpsertRequest
            {
                DataKey = dataKey,
                ExecutablePath = exePath, // Will be null if only name was provided
                ExecutableName = executableName,
                DisplayName = string.IsNullOrWhiteSpace(displayName) ? null : displayName.Trim(),
                IsEnabled = string.Equals(enable, "启用", StringComparison.Ordinal),
                HdrEnabled = string.Equals(hdr, "自动开启 HDR", StringComparison.Ordinal)
            });

            _console.MarkupLine($"[green]已保存[/]：{Markup.Escape(saved.ExecutableName ?? executableName)} (DataKey: {Markup.Escape(saved.DataKey)})");
            return Task.CompletedTask;
        }

        private Task EditGameAsync()
        {
            var configs = _gameCatalogService.GetAll();
            if (configs.Count == 0)
            {
                _console.MarkupLine("[italic grey]没有可以修改的游戏。[/]");
                return Task.CompletedTask;
            }

            var title = "选择需要修改的游戏";
            var choices = configs
                .Select(c => c.DataKey)
                .OrderBy(k => k, StringComparer.OrdinalIgnoreCase)
                .ToList();
            var prompt = new SelectionPrompt<string>
            {
                PageSize = 10
            };
            prompt.Title(title);
            prompt.AddChoices(choices);

            var selectedDataKey = _promptUI.PromptSelection(prompt, choices, value => Markup.Escape(value), title);
            var cfg = configs.FirstOrDefault(entry => string.Equals(entry.DataKey, selectedDataKey, StringComparison.OrdinalIgnoreCase));
            if (cfg is null)
            {
                _console.MarkupLine("[red]未找到对应的配置。[/]");
                return Task.CompletedTask;
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
            var pathInput = _promptUI.Prompt(pathPrompt);
            pathInput = pathInput.Trim().Trim('"');

            string? newExecutablePath = cfg.ExecutablePath;
            var clearExecutablePath = false;
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
                    clearExecutablePath = true;
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
            var displayName = _promptUI.Prompt(displayNamePrompt);

            // Prompt for automation enable
            var enableTitle = "是否启用自动化？";
            var enableChoices = cfg.IsEnabled
                ? new[] { "启用", "禁用" }
                : new[] { "禁用", "启用" };
            var enablePrompt = new SelectionPrompt<string>();
            enablePrompt.Title(enableTitle);
            enablePrompt.AddChoices(enableChoices);
            var enable = _promptUI.PromptSelection(enablePrompt, enableChoices, value => Markup.Escape(value), enableTitle);

            // Prompt for HDR
            var hdrTitle = "在游戏运行时如何控制 HDR？";
            var hdrChoices = cfg.HdrEnabled
                ? new[] { "自动开启 HDR", "不自动开启" }
                : new[] { "不自动开启", "自动开启 HDR" };
            var hdrPrompt = new SelectionPrompt<string>();
            hdrPrompt.Title(hdrTitle);
            hdrPrompt.AddChoices(hdrChoices);
            var hdr = _promptUI.PromptSelection(hdrPrompt, hdrChoices, value => Markup.Escape(value), hdrTitle);

            // Update configuration
            cfg.ExecutablePath = newExecutablePath;
            cfg.DisplayName = string.IsNullOrWhiteSpace(displayName) ? null : displayName.Trim();
            cfg.IsEnabled = string.Equals(enable, "启用", StringComparison.Ordinal);
            cfg.HdrEnabled = string.Equals(hdr, "自动开启 HDR", StringComparison.Ordinal);

            var updated = _gameCatalogService.Update(cfg.DataKey, new GameEntryUpsertRequest
            {
                ExecutableName = cfg.ExecutableName,
                ExecutablePath = cfg.ExecutablePath,
                ClearExecutablePath = clearExecutablePath,
                DisplayName = cfg.DisplayName,
                ClearDisplayName = string.IsNullOrWhiteSpace(displayName),
                IsEnabled = cfg.IsEnabled,
                HdrEnabled = cfg.HdrEnabled
            });
            _console.MarkupLine("[green]配置已更新。[/]");

            // Display updated configuration
            _console.WriteLine();
            _console.MarkupLine("[yellow]更新后的配置：[/]");
            _console.MarkupLine($"  DataKey: {Markup.Escape(updated.DataKey)}");
            if (!string.IsNullOrWhiteSpace(updated.ExecutablePath))
            {
                _console.MarkupLine($"  完整路径: {Markup.Escape(updated.ExecutablePath)}");
            }
            if (!string.IsNullOrWhiteSpace(updated.DisplayName))
            {
                _console.MarkupLine($"  显示名称: {Markup.Escape(updated.DisplayName)}");
            }

            return Task.CompletedTask;
        }

        private async Task RemoveGameAsync()
        {
            await Task.CompletedTask.ConfigureAwait(false);

            var configs = _gameCatalogService.GetAll();
            if (configs.Count == 0)
            {
                _console.MarkupLine("[italic grey]当前没有可删除的游戏。[/]");
                return;
            }

            var title = "选择要删除的游戏";
            var choices = configs
                .Select(c => c.DataKey)
                .OrderBy(k => k, StringComparer.OrdinalIgnoreCase)
                .ToList();
            var prompt = new SelectionPrompt<string>
            {
                PageSize = 10
            };
            prompt.Title(title);
            prompt.AddChoices(choices);

            var selectedDataKey = _promptUI.PromptSelection(prompt, choices, value => Markup.Escape(value), title);
            if (!configs.Any(entry => string.Equals(entry.DataKey, selectedDataKey, StringComparison.OrdinalIgnoreCase)))
            {
                _console.MarkupLine("[red]未找到对应的配置。[/]");
                return;
            }

            var confirm = _promptUI.PromptConfirm($"确定要删除 [bold]{Markup.Escape(selectedDataKey)}[/] 吗？");
            if (!confirm)
            {
                return;
            }

            _gameCatalogService.Delete(selectedDataKey);
            _console.MarkupLine("[yellow]已移除该游戏。[/]");
        }

    }
}
