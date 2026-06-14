using GameHelper.ConsoleHost.Utilities;
using GameHelper.Core.Abstractions;
using GameHelper.Core.Models;
using Spectre.Console;

namespace GameHelper.ConsoleHost.Interactive;

/// <summary>
/// Displays playtime statistics in the interactive console shell.
/// </summary>
internal sealed class StatisticsUI
{
    private readonly IAnsiConsole _console;
    private readonly PromptUI _promptUI;
    private readonly IStatisticsService _statisticsService;

    public StatisticsUI(IAnsiConsole console, PromptUI promptUI, IStatisticsService statisticsService)
    {
        _console = console ?? throw new ArgumentNullException(nameof(console));
        _promptUI = promptUI ?? throw new ArgumentNullException(nameof(promptUI));
        _statisticsService = statisticsService ?? throw new ArgumentNullException(nameof(statisticsService));
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

        var list = GetStats(filter);
        if (list.Count == 0)
        {
            if (string.IsNullOrWhiteSpace(filter))
            {
                _console.MarkupLine("[italic grey]尚未生成任何游戏时长数据。[/]");
            }
            else
            {
                _console.MarkupLine($"[yellow]未找到与 [bold]{Markup.Escape(filter!)}[/] 匹配的记录。[/]");
            }

            _promptUI.WaitForMenuReturn();
            return;
        }

        var projected = list.Select(g => new
        {
            Name = g.DisplayName ?? g.GameName,
            g.TotalMinutes,
            g.RecentMinutes,
            Sessions = g.SessionCount
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
            table.AddRow(
                "[bold]TOTAL[/]",
                $"[bold]{DurationFormatter.Format(totalAll)}[/]",
                $"[bold]{DurationFormatter.Format(totalRecent)}[/]",
                $"[bold]{totalSessions}[/]");
        }

        _console.Write(table);
        _promptUI.WaitForMenuReturn();
    }

    private IReadOnlyList<GameStatsSummary> GetStats(string? filter)
    {
        if (string.IsNullOrWhiteSpace(filter))
        {
            return _statisticsService.GetOverview();
        }

        var details = _statisticsService.GetDetails(filter);
        return details is null
            ? Array.Empty<GameStatsSummary>()
            : new List<GameStatsSummary> { details };
    }
}
