using System.Text;
using GameHelper.ConsoleHost.Utilities;
using GameHelper.Core.Abstractions;
using GameHelper.Core.Models;

namespace GameHelper.ConsoleHost.Commands;

public static class StatsCommand
{
    public static void Run(IStatisticsService statisticsService, string[] args)
    {
        ArgumentNullException.ThrowIfNull(statisticsService);

        var parseResult = ParseArgs(args);
        if (!parseResult.IsValid)
        {
            Console.WriteLine(parseResult.ErrorMessage);
            return;
        }

        var filterGame = parseResult.FilterGame;

        try
        {
            IReadOnlyList<GameStatsSummary> list;
            if (string.IsNullOrWhiteSpace(filterGame))
            {
                list = statisticsService.GetOverview();
            }
            else
            {
                var details = statisticsService.GetDetails(filterGame);
                list = details is null
                    ? new List<GameStatsSummary>()
                    : new List<GameStatsSummary> { details };
            }

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

    private static StatsArgs ParseArgs(string[] args)
    {
        if (args.Length == 0)
        {
            return StatsArgs.Valid(null);
        }

        if (!string.Equals(args[0], "--game", StringComparison.OrdinalIgnoreCase))
        {
            return StatsArgs.Invalid("Unknown stats option. Usage: stats [--game <name>]");
        }

        if (args.Length < 2 || string.IsNullOrWhiteSpace(args[1]))
        {
            return StatsArgs.Invalid("Missing value after --game.");
        }

        if (args.Length > 2)
        {
            return StatsArgs.Invalid("Too many arguments for stats. Usage: stats [--game <name>]");
        }

        return StatsArgs.Valid(args[1].Trim());
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

    private sealed class StatsArgs
    {
        private StatsArgs(bool isValid, string? filterGame, string? errorMessage)
        {
            IsValid = isValid;
            FilterGame = filterGame;
            ErrorMessage = errorMessage;
        }

        public bool IsValid { get; }

        public string? FilterGame { get; }

        public string? ErrorMessage { get; }

        public static StatsArgs Valid(string? filterGame) => new(true, filterGame, null);

        public static StatsArgs Invalid(string errorMessage) => new(false, null, errorMessage);
    }
}
