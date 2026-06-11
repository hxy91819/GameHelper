using System;
using System.IO;
using GameHelper.ConsoleHost.Utilities;
using GameHelper.Core.Utilities;
using GameHelper.Infrastructure.Providers;
using GameHelper.Infrastructure.Validators;
using Spectre.Console;

namespace GameHelper.ConsoleHost.Interactive
{
    /// <summary>
    /// 提供工具与诊断功能：配置格式转换、YAML 配置校验。
    /// </summary>
    internal sealed class ToolsUI
    {
        private readonly IAnsiConsole _console;
        private readonly PromptUI _promptUI;

        public ToolsUI(IAnsiConsole console, PromptUI promptUI)
        {
            _console = console ?? throw new ArgumentNullException(nameof(console));
            _promptUI = promptUI ?? throw new ArgumentNullException(nameof(promptUI));
        }

        public void HandleTools()
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

                var choice = _promptUI.PromptSelection(
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
    }

    internal enum ToolAction
    {
        ConvertConfig,
        ValidateConfig,
        Back
    }
}
