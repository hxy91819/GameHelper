using System.Text;
using GameHelper.ConsoleHost.Utilities;
using GameHelper.Core.Abstractions;
using GameHelper.Core.Models;
using Microsoft.Extensions.DependencyInjection;

namespace GameHelper.ConsoleHost.Commands;

public static class StatsCommand
{
    public static void Run(IServiceProvider services, string[] args)
    {
        string? filterGame = null;
        if (args.Length >= 2 && args[0] == "--game")
        {
            filterGame = args[1];
        }

        try
        {
            using var scope = services.CreateScope();
            var statisticsService = scope.ServiceProvider.GetRequiredService<IStatisticsService>();

            var list = string.IsNullOrWhiteSpace(filterGame)
                ? statisticsService.GetOverview()
                : statisticsService.GetDetails(filterGame) is { } details
                    ? new List<GameStatsSummary> { details }
                    : new List<GameStatsSummary>();

            if (list.Count == 0)
            {
                Console.WriteLine(string.IsNullOrWhiteSpace(filterGame) ? "No playtime data yet." : "No matching playtime.");
                return;
            }

            DisplayStats(list, filterGame);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Failed to read stats: {ex.Message}");
        }
    }

    private static void DisplayStats(IReadOnlyList<GameStatsSummary> stats, string? filterGame)
    {
        var header = new[] { "Game", "Total", "Last 2 Weeks", "Sessions" };
        var rows = stats.Select(item => new[]
        {
            item.DisplayName ?? item.GameName,
            DurationFormatter.Format(item.TotalMinutes),
            DurationFormatter.Format(item.RecentMinutes),
            item.SessionCount.ToString()
        }).ToList();

        if (string.IsNullOrWhiteSpace(filterGame))
        {
            rows.Add(new[]
            {
                "TOTAL",
                DurationFormatter.Format(stats.Sum(item => item.TotalMinutes)),
                DurationFormatter.Format(stats.Sum(item => item.RecentMinutes)),
                stats.Sum(item => item.SessionCount).ToString()
            });
        }

        var widths = new int[header.Length];
        for (var i = 0; i < header.Length; i++)
        {
            widths[i] = DisplayWidth.Measure(header[i]);
        }

        foreach (var row in rows)
        {
            for (var i = 0; i < row.Length; i++)
            {
                var width = DisplayWidth.Measure(row[i]);
                if (width > widths[i])
                {
                    widths[i] = width;
                }
            }
        }

        string Separator()
        {
            var builder = new StringBuilder();
            builder.Append('+');
            foreach (var width in widths)
            {
                builder.Append(new string('-', width + 2));
                builder.Append('+');
            }

            return builder.ToString();
        }

        string Row(IReadOnlyList<string> columns)
        {
            var builder = new StringBuilder();
            builder.Append('|');
            for (var i = 0; i < columns.Count; i++)
            {
                builder.Append(' ');
                builder.Append(DisplayWidth.PadRight(columns[i], widths[i]));
                builder.Append(' ');
                builder.Append('|');
            }

            return builder.ToString();
        }

        var separator = Separator();
        Console.WriteLine(separator);
        Console.WriteLine(Row(header));
        Console.WriteLine(separator);
        foreach (var row in rows)
        {
            Console.WriteLine(Row(row));
        }

        Console.WriteLine(separator);
    }
}
