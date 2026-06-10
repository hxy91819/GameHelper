using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using GameHelper.ConsoleHost.Models;
using GameHelper.ConsoleHost.Services;
using GameHelper.ConsoleHost.Utilities;
using GameHelper.Core.Abstractions;
using GameHelper.Core.Models;
using GameHelper.Core.Utilities;
using Spectre.Console;

namespace GameHelper.ConsoleHost.Interactive
{
    /// <summary>
    /// 负责展示游戏时长统计数据。
    /// </summary>
    internal sealed class StatisticsUI
    {
        private readonly IAnsiConsole _console;
        private readonly PromptUI _promptUI;
        private readonly IConfigProvider _configProvider;

        public StatisticsUI(IAnsiConsole console, PromptUI promptUI, IConfigProvider configProvider)
        {
            _console = console ?? throw new ArgumentNullException(nameof(console));
            _promptUI = promptUI ?? throw new ArgumentNullException(nameof(promptUI));
            _configProvider = configProvider ?? throw new ArgumentNullException(nameof(configProvider));
        }

        public void ShowStatistics()
        {
            var statsRule = new Rule("[yellow]游戏时长统计[/]")
            {
                Style = new Style(Color.Grey),
                Justification = Justify.Left
            };
            _console.Write(statsRule);

            var filterPrompt = new TextPrompt<string>("输入要筛选的游戏名称（留空表示全部）") { AllowEmpty = true };
            var filter = _promptUI.Prompt(filterPrompt);
            filter = string.IsNullOrWhiteSpace(filter) ? null : filter.Trim();

            if (!TryLoadPlaytimeData(out var items, out var source))
            {
                _console.MarkupLine("[italic grey]尚未生成任何游戏时长数据。[/]");
                _promptUI.WaitForMenuReturn();
                return;
            }

            var list = string.IsNullOrWhiteSpace(filter)
                ? items
                : items.Where(i => string.Equals(i.GameName, filter, StringComparison.OrdinalIgnoreCase)).ToList();

            if (list.Count == 0)
            {
                _console.MarkupLine($"[yellow]未找到与 [bold]{Markup.Escape(filter!)}[/] 匹配的记录。[/]");
                _promptUI.WaitForMenuReturn();
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
                _promptUI.WaitForMenuReturn();
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

            _promptUI.WaitForMenuReturn();
        }

        private static bool TryLoadPlaytimeData(out List<GameItem> items, out string source)
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

        private static string ResolveDisplayName(string key, GameConfigLookup lookup)
        {
            var cfg = lookup.Resolve(key);
            return !string.IsNullOrWhiteSpace(cfg?.DisplayName) ? cfg.DisplayName! : key;
        }
    }
}
