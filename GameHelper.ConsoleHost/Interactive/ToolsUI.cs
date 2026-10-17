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
                var title = "[bold green]Tools & diagnostics[/]";
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
                        ToolAction.ConvertConfig => "🔄  Convert legacy JSON config to YAML",
                        ToolAction.ValidateConfig => "✅  Validate current YAML config",
                        ToolAction.Back => "⬅️  Back",
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
                    _console.MarkupLine($"[yellow]Legacy JSON config not found at {Markup.Escape(jsonPath)}.[/]");
                    return;
                }

                _console.Status().Start("Converting config...", ctx =>
                {
                    var jsonProvider = new JsonConfigProvider(jsonPath);
                    var data = jsonProvider.Read();
                    ctx.Status("Writing YAML...");
                    var yamlProvider = new YamlConfigProvider(ymlPath);
                    yamlProvider.Change(config =>
                    {
                        config.ProcessMonitorType = data.ProcessMonitorType;
                        config.AutoStartInteractiveMonitor = data.AutoStartInteractiveMonitor;
                        config.LaunchOnSystemStartup = data.LaunchOnSystemStartup;
                        config.Games = data.Games;
                    });
                });

                _console.MarkupLine($"[green]Conversion completed[/]; YAML written to {Markup.Escape(ymlPath)}.");
            }
            catch (Exception ex)
            {
                _console.MarkupLine($"[red]Conversion failed: {Markup.Escape(ex.Message)}[/]");
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
                table.AddColumn("Metric");
                table.AddColumn("Value");
                table.AddRow("Config path", Markup.Escape(path));
                table.AddRow("Game count", result.GameCount.ToString());
                table.AddRow("Duplicate entries", result.DuplicateCount.ToString());
                table.AddRow("Status", result.IsValid ? "[green]Passed[/]" : "[red]Errors found[/]");

                _console.Write(table);

                if (result.Warnings.Count > 0)
                {
                    _console.MarkupLine("[yellow]Warnings:[/]");
                    foreach (var warning in result.Warnings)
                    {
                        _console.MarkupLine($"  • {Markup.Escape(warning)}");
                    }
                }

                if (result.Errors.Count > 0)
                {
                    _console.MarkupLine("[red]Errors:[/]");
                    foreach (var error in result.Errors)
                    {
                        _console.MarkupLine($"  • {Markup.Escape(error)}");
                    }
                }
            }
            catch (Exception ex)
            {
                _console.MarkupLine($"[red]Validation failed: {Markup.Escape(ex.Message)}[/]");
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
