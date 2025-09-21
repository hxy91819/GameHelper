using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using GameHelper.ConsoleHost.Models;
using GameHelper.ConsoleHost.Services;
using GameHelper.ConsoleHost.Utilities;
using GameHelper.Core.Abstractions;
using GameHelper.Core.Models;
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

        private enum ToolAction
        {
            ConvertConfig,
            ValidateConfig,
            Back
        }

        private readonly IHost _host;
        private readonly ParsedArguments _arguments;
        private readonly IAnsiConsole _console;
        private readonly IConfigProvider _configProvider;
        private readonly IAppConfigProvider _appConfigProvider;
        private readonly InteractiveScript? _script;
        private readonly Func<IHost, Task> _hostRunner;

        public InteractiveShell(IHost host, ParsedArguments arguments, IAnsiConsole? console = null, InteractiveScript? script = null, Func<IHost, Task>? hostRunner = null)
        {
            _host = host ?? throw new ArgumentNullException(nameof(host));
            _arguments = arguments ?? throw new ArgumentNullException(nameof(arguments));
            _console = console ?? AnsiConsole.Console;
            _configProvider = host.Services.GetRequiredService<IConfigProvider>();
            _appConfigProvider = host.Services.GetRequiredService<IAppConfigProvider>();
            _script = script;
            _hostRunner = hostRunner ?? (h => h.RunAsync());
        }

        public async Task RunAsync()
        {
            Console.OutputEncoding = Encoding.UTF8;
            try
            {
                Console.Title = "GameHelper 互动命令行";
            }
            catch
            {
                // Some environments (e.g., CI, redirected output) do not allow setting the console title.
            }

            RenderWelcome();

            while (true)
            {
                var action = PromptMainMenu();
                switch (action)
                {
                    case MainMenuAction.Monitor:
                        if (await LaunchMonitorAsync().ConfigureAwait(false))
                        {
                            return;
                        }
                        break;

                    case MainMenuAction.Configuration:
                        await HandleConfigurationAsync().ConfigureAwait(false);
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

        private void RenderWelcome()
        {
            _console.Clear();

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
            infoTable.Caption("使用方向键选择功能，回车确认");
            _console.Write(infoTable);

            _console.WriteLine();
        }

        private MainMenuAction PromptMainMenu()
        {
            var prompt = new SelectionPrompt<MainMenuAction>()
            {
                Title = "[bold green]请选择要执行的操作：[/]",
                PageSize = 6
            };

            prompt.AddChoices(Enum.GetValues<MainMenuAction>());
            prompt.UseConverter(action => action switch
            {
                MainMenuAction.Monitor => "🚀  启动实时监控",
                MainMenuAction.Configuration => "🛠  管理游戏配置",
                MainMenuAction.Statistics => "📊  查看游戏时长统计",
                MainMenuAction.Tools => "🧰  工具与诊断",
                MainMenuAction.Exit => "⬅️  退出",
                _ => action.ToString()
            });

            return Prompt(prompt);
        }

        private async Task<bool> LaunchMonitorAsync()
        {
            var snapshotBefore = CaptureSessionSnapshot();

            var monitorRule = new Rule("[yellow]实时监控[/]")
            {
                Style = new Style(Color.Grey),
                Justification = Justify.Left
            };
            _console.Write(monitorRule);

            var monitorInfo = new Grid();
            monitorInfo.AddColumn(new GridColumn().NoWrap());
            monitorInfo.AddRow(new Markup($"将以 [bold]{Markup.Escape(GetMonitorModeDescription())}[/] 运行监控"));
            monitorInfo.AddRow(new Markup("开始后可按 [bold]Ctrl + C[/] 停止并返回桌面"));
            monitorInfo.AddRow(new Markup($"配置文件位置：{Markup.Escape(GetConfigPathDescription())}"));
            monitorInfo.AddRow(new Markup("后台服务会自动加载启用的游戏列表进行白名单监控"));

            _console.Write(new Panel(monitorInfo)
            {
                Header = new PanelHeader("执行前确认"),
                Border = BoxBorder.Rounded
            });

            RenderMonitorHistory(snapshotBefore);
            _console.WriteLine();

            var confirm = Prompt(new SelectionPrompt<string>()
                .Title("是否立即启动实时监控？")
                .AddChoices("开始监控", "返回菜单"));

            if (!string.Equals(confirm, "开始监控", StringComparison.Ordinal))
            {
                return false;
            }

            _console.MarkupLine("[bold green]正在启动监控... 按 Ctrl+C 可随时停止。[/]");
            _console.WriteLine();

            SessionSnapshot snapshotAfter;
            try
            {
                await _hostRunner(_host).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Expected when the user stops the host via Ctrl+C.
            }

            _console.MarkupLine("[grey]监控已停止，正在汇总本次游玩...[/]");
            _console.WriteLine();

            snapshotAfter = CaptureSessionSnapshot();
            RenderSessionSummary(snapshotBefore, snapshotAfter);
            _console.WriteLine();

            return true;
        }

        private async Task HandleConfigurationAsync()
        {
            while (true)
            {
                var prompt = new SelectionPrompt<ConfigAction>()
                {
                    Title = "[bold green]配置管理[/]",
                    PageSize = 5
                };

                prompt.AddChoices(Enum.GetValues<ConfigAction>());
                prompt.UseConverter(action => action switch
                {
                    ConfigAction.View => "📋  查看当前配置",
                    ConfigAction.Add => "➕  添加新游戏",
                    ConfigAction.Edit => "✏️  修改现有游戏",
                    ConfigAction.Remove => "🗑  删除游戏",
                    ConfigAction.Back => "⬅️  返回上一级",
                    _ => action.ToString()
                });

                var selection = Prompt(prompt);
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
            var configs = LoadConfigs();
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
            table.AddColumn("可执行文件");
            table.AddColumn("显示名称");
            table.AddColumn("自动化");
            table.AddColumn("HDR");

            foreach (var entry in configs.OrderBy(e => e.Key, StringComparer.OrdinalIgnoreCase))
            {
                var cfg = entry.Value;
                table.AddRow(
                    Markup.Escape(entry.Key),
                    string.IsNullOrWhiteSpace(cfg.Alias) ? "-" : Markup.Escape(cfg.Alias!),
                    cfg.IsEnabled ? "[green]启用[/]" : "[red]禁用[/]",
                    cfg.HDREnabled ? "[green]开启[/]" : "[yellow]保持关闭[/]");
            }

            _console.Write(table);
        }

        private async Task AddGameAsync()
        {
            var configs = LoadConfigs();

            var exe = Prompt(new TextPrompt<string>("请输入游戏的可执行文件名 (例如 [green]game.exe[/])")
                .Validate(name => string.IsNullOrWhiteSpace(name)
                    ? ConsoleValidationResult.Error("文件名不能为空。")
                    : ConsoleValidationResult.Success()));

            configs.TryGetValue(exe, out var existingConfig);

            var defaultAlias = existingConfig != null && !string.IsNullOrWhiteSpace(existingConfig.Alias)
                ? existingConfig.Alias!
                : string.Empty;
            var aliasPrompt = new TextPrompt<string>("输入显示名称（可选，直接回车跳过）")
                .AllowEmpty()
                .DefaultValue(defaultAlias);
            var alias = Prompt(aliasPrompt);

            var enablePrompt = new SelectionPrompt<string>()
                .Title("是否启用自动化？");
            if (existingConfig?.IsEnabled == false)
            {
                enablePrompt.AddChoices("禁用", "启用");
            }
            else
            {
                enablePrompt.AddChoices("启用", "禁用");
            }
            var enable = Prompt(enablePrompt);

            var hdrPrompt = new SelectionPrompt<string>()
                .Title("在游戏运行时如何控制 HDR？");
            var defaultHdrEnabled = existingConfig?.HDREnabled ?? true;
            if (defaultHdrEnabled)
            {
                hdrPrompt.AddChoices("自动开启 HDR", "保持关闭");
            }
            else
            {
                hdrPrompt.AddChoices("保持关闭", "自动开启 HDR");
            }
            var hdr = Prompt(hdrPrompt);

            configs[exe] = new GameConfig
            {
                Name = exe,
                Alias = string.IsNullOrWhiteSpace(alias) ? null : alias.Trim(),
                IsEnabled = string.Equals(enable, "启用", StringComparison.Ordinal),
                HDREnabled = string.Equals(hdr, "自动开启 HDR", StringComparison.Ordinal)
            };

            await PersistAsync(configs).ConfigureAwait(false);
            _console.MarkupLine($"[green]已保存[/]：{Markup.Escape(exe)}");
        }

        private async Task EditGameAsync()
        {
            var configs = LoadConfigs();
            if (configs.Count == 0)
            {
                _console.MarkupLine("[italic grey]没有可以修改的游戏。[/]");
                return;
            }

            var prompt = new SelectionPrompt<string>()
                .Title("选择需要修改的游戏")
                .PageSize(10)
                .UseConverter(value => Markup.Escape(value))
                .AddChoices(configs.Keys.OrderBy(k => k, StringComparer.OrdinalIgnoreCase));

            var exe = Prompt(prompt);
            if (!configs.TryGetValue(exe, out var cfg))
            {
                _console.MarkupLine("[red]未找到对应的配置。[/]");
                return;
            }

            var aliasPrompt = new TextPrompt<string>("更新显示名称（可留空）")
                .AllowEmpty()
                .DefaultValue(cfg.Alias ?? string.Empty);
            var alias = Prompt(aliasPrompt);

            var enablePrompt = new SelectionPrompt<string>()
                .Title("是否启用自动化？");
            if (cfg.IsEnabled)
            {
                enablePrompt.AddChoices("启用", "禁用");
            }
            else
            {
                enablePrompt.AddChoices("禁用", "启用");
            }
            var enable = Prompt(enablePrompt);

            var hdrPrompt = new SelectionPrompt<string>()
                .Title("在游戏运行时如何控制 HDR？");
            if (cfg.HDREnabled)
            {
                hdrPrompt.AddChoices("自动开启 HDR", "保持关闭");
            }
            else
            {
                hdrPrompt.AddChoices("保持关闭", "自动开启 HDR");
            }
            var hdr = Prompt(hdrPrompt);

            cfg.Alias = string.IsNullOrWhiteSpace(alias) ? null : alias.Trim();
            cfg.IsEnabled = string.Equals(enable, "启用", StringComparison.Ordinal);
            cfg.HDREnabled = string.Equals(hdr, "自动开启 HDR", StringComparison.Ordinal);

            configs[exe] = cfg;
            await PersistAsync(configs).ConfigureAwait(false);
            _console.MarkupLine("[green]配置已更新。[/]");
        }

        private async Task RemoveGameAsync()
        {
            var configs = LoadConfigs();
            if (configs.Count == 0)
            {
                _console.MarkupLine("[italic grey]当前没有可删除的游戏。[/]");
                return;
            }

            var prompt = new SelectionPrompt<string>()
                .Title("选择要删除的游戏")
                .PageSize(10)
                .UseConverter(value => Markup.Escape(value))
                .AddChoices(configs.Keys.OrderBy(k => k, StringComparer.OrdinalIgnoreCase));

            var exe = Prompt(prompt);

            var confirm = PromptConfirm($"确定要删除 [bold]{Markup.Escape(exe)}[/] 吗？");
            if (!confirm)
            {
                return;
            }

            configs.Remove(exe);
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
                return;
            }

            var list = string.IsNullOrWhiteSpace(filter)
                ? items
                : items.Where(i => string.Equals(i.GameName, filter, StringComparison.OrdinalIgnoreCase)).ToList();

            if (list.Count == 0)
            {
                _console.MarkupLine($"[yellow]未找到与 [bold]{Markup.Escape(filter!)}[/] 匹配的记录。[/]");
                return;
            }

            var cfg = new Dictionary<string, GameConfig>(_configProvider.Load(), StringComparer.OrdinalIgnoreCase);
            var now = DateTime.Now;
            var cutoff = now.AddDays(-14);

            var projected = list.Select(g => new
            {
                Name = cfg.TryGetValue(g.GameName, out var gc) && !string.IsNullOrWhiteSpace(gc.Alias)
                    ? gc.Alias!
                    : g.GameName,
                TotalMinutes = g.Sessions?.Sum(s => s.DurationMinutes) ?? 0,
                RecentMinutes = g.Sessions?.Where(s => s.EndTime >= cutoff).Sum(s => s.DurationMinutes) ?? 0,
                Sessions = g.Sessions?.Count ?? 0
            })
            .OrderByDescending(x => x.RecentMinutes)
            .ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

            if (projected.Count == 0)
            {
                _console.MarkupLine("[italic grey]没有可展示的数据。[/]");
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
        }

        private void HandleTools()
        {
            while (true)
            {
                var prompt = new SelectionPrompt<ToolAction>()
                {
                    Title = "[bold green]工具与诊断[/]",
                    PageSize = 4
                };

                prompt.AddChoices(Enum.GetValues<ToolAction>());
                prompt.UseConverter(action => action switch
                {
                    ToolAction.ConvertConfig => "🔄  将旧版 JSON 配置转换为 YAML",
                    ToolAction.ValidateConfig => "✅  校验当前 YAML 配置",
                    ToolAction.Back => "⬅️  返回上一级",
                    _ => action.ToString()
                });

                var choice = Prompt(prompt);
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
                string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
                string dir = Path.Combine(appData, "GameHelper");
                string jsonPath = Path.Combine(dir, "config.json");
                string ymlPath = Path.Combine(dir, "config.yml");

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
            string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            string dir = Path.Combine(appData, "GameHelper");
            string csvFile = Path.Combine(dir, "playtime.csv");
            string jsonFile = Path.Combine(dir, "playtime.json");

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
            var keys = new HashSet<SessionKey>();
            var records = new List<SessionRecord>();

            foreach (var item in items)
            {
                var displayName = configs.TryGetValue(item.GameName, out var cfg) && !string.IsNullOrWhiteSpace(cfg.Alias)
                    ? cfg.Alias!
                    : item.GameName;

                foreach (var session in item.Sessions)
                {
                    var record = new SessionRecord(item.GameName, displayName, session.StartTime, session.EndTime, session.DurationMinutes);
                    keys.Add(record.Key);
                    records.Add(record);
                }
            }

            return new SessionSnapshot(keys, records, source);
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
