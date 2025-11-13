using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using GameHelper.ConsoleHost.Models;
using GameHelper.ConsoleHost.Services;
using GameHelper.ConsoleHost.Utilities;
using GameHelper.Core.Models;
using GameHelper.Infrastructure.Providers;

namespace GameHelper.ConsoleHost.Commands
{
    public static class StatsCommand
    {
        public static void Run(string[] args)
        {
            string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            string dir = Path.Combine(appData, "GameHelper");
            string csvFile = Path.Combine(dir, "playtime.csv");
            string jsonFile = Path.Combine(dir, "playtime.json");

            string? filterGame = null;
            if (args.Length >= 2 && args[0] == "--game") filterGame = args[1];

            try
            {
                List<GameItem> items;
                
                // Try CSV first, fallback to JSON
                if (File.Exists(csvFile))
                {
                    items = PlaytimeDataReader.ReadFromCsv(csvFile);
                }
                else if (File.Exists(jsonFile))
                {
                    items = PlaytimeDataReader.ReadFromJson(jsonFile);
                }
                else
                {
                    Console.WriteLine("No playtime data yet.");
                    return;
                }

                var list = string.IsNullOrWhiteSpace(filterGame)
                    ? items
                    : items.Where(i => string.Equals(i.GameName, filterGame, StringComparison.OrdinalIgnoreCase)).ToList();

                if (list.Count == 0)
                {
                    Console.WriteLine("No matching playtime.");
                    return;
                }

                DisplayStats(list, filterGame);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Failed to read stats: {ex.Message}");
            }
        }

        private static void DisplayStats(List<GameItem> games, string? filterGame)
        {
            // load config to resolve DisplayName for display (YAML only)
            var cfgProvider = new YamlConfigProvider();
            var cfg = new Dictionary<string, GameConfig>(cfgProvider.Load(), StringComparer.OrdinalIgnoreCase);

            // compute recent window
            var now = DateTime.Now;
            var cutoff = now.AddDays(-14);

            // project with computed fields
            // Display priority: DisplayName > DataKey > CSV original value
            var projected = games
                .Select(g => new
                {
                    Game = g,
                    Name = cfg.TryGetValue(g.GameName, out var gc) && !string.IsNullOrWhiteSpace(gc.DisplayName)
                        ? gc.DisplayName!
                        : (cfg.TryGetValue(g.GameName, out gc) ? gc.DataKey : g.GameName),
                    TotalMinutes = g.Sessions?.Sum(s => s.DurationMinutes) ?? 0,
                    RecentMinutes = g.Sessions?.Where(s => s.EndTime >= cutoff).Sum(s => s.DurationMinutes) ?? 0,
                    SessionCount = g.Sessions?.Count ?? 0
                })
                .OrderByDescending(x => x.RecentMinutes)
                .ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (projected.Count == 0)
            {
                Console.WriteLine("No matching playtime.");
                return;
            }

            // prepare ASCII table columns
            var header = new[] { "Game", "Total", "Last 2 Weeks", "Sessions" };
            var rows = projected.Select(p => new[]
            {
                p.Name,
                DurationFormatter.Format(p.TotalMinutes),
                DurationFormatter.Format(p.RecentMinutes),
                p.SessionCount.ToString()
            }).ToList();

            // add TOTAL row when not filtered
            if (string.IsNullOrWhiteSpace(filterGame))
            {
                var totalAll = projected.Sum(p => p.TotalMinutes);
                var totalRecent = projected.Sum(p => p.RecentMinutes);
                rows.Add(new[] { "TOTAL", DurationFormatter.Format(totalAll), DurationFormatter.Format(totalRecent), projected.Sum(p => p.SessionCount).ToString() });
            }

            // compute column widths
            int[] widths = new int[header.Length];
            for (int i = 0; i < header.Length; i++)
            {
                widths[i] = DisplayWidth.Measure(header[i]);
            }
            foreach (var row in rows)
            {
                for (int i = 0; i < row.Length; i++)
                {
                    int cellWidth = DisplayWidth.Measure(row[i]);
                    if (cellWidth > widths[i]) widths[i] = cellWidth;
                }
            }

            // helpers
            string Sep()
            {
                var sb = new StringBuilder();
                sb.Append('+');
                for (int i = 0; i < widths.Length; i++)
                {
                    sb.Append(new string('-', widths[i] + 2));
                    sb.Append('+');
                }
                return sb.ToString();
            }
            string Line(IReadOnlyList<string> cols)
            {
                var sb = new StringBuilder();
                sb.Append('|');
                for (int i = 0; i < cols.Count; i++)
                {
                    sb.Append(' ');
                    sb.Append(DisplayWidth.PadRight(cols[i], widths[i]));
                    sb.Append(' ');
                    sb.Append('|');
                }
                return sb.ToString();
            }

            // print table
            var sep = Sep();
            Console.WriteLine(sep);
            Console.WriteLine(Line(header));
            Console.WriteLine(sep);
            foreach (var row in rows)
            {
                Console.WriteLine(Line(row));
            }
            Console.WriteLine(sep);
        }
    }
}