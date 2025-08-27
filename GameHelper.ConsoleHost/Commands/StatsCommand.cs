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
            // load config to resolve Alias for display (YAML only)
            var cfgProvider = new YamlConfigProvider();
            var cfg = new Dictionary<string, GameConfig>(cfgProvider.Load(), StringComparer.OrdinalIgnoreCase);

            long total = 0;
            foreach (var g in games.OrderBy(g => g.GameName, StringComparer.OrdinalIgnoreCase))
            {
                var minutes = g.Sessions?.Sum(s => s.DurationMinutes) ?? 0;
                total += minutes;
                var display = cfg.TryGetValue(g.GameName, out var gc) && !string.IsNullOrWhiteSpace(gc.Alias)
                    ? gc.Alias!
                    : g.GameName;
                var formatted = DurationFormatter.Format(minutes);
                Console.WriteLine($"{display}: {formatted}, sessions={g.Sessions?.Count ?? 0}");
            }
            
            if (string.IsNullOrWhiteSpace(filterGame))
            {
                Console.WriteLine($"TOTAL: {DurationFormatter.Format(total)}");
            }
        }
    }
}