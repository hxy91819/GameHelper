using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using GameHelper.ConsoleHost.Models;
using GameHelper.ConsoleHost.Utilities;

namespace GameHelper.ConsoleHost.Services
{
    public static class PlaytimeDataReader
    {
        public static List<GameItem> ReadFromCsv(string csvFile)
        {
            var gameData = new Dictionary<string, GameItem>(StringComparer.OrdinalIgnoreCase);
            
            using var reader = new StreamReader(csvFile);
            string? line = reader.ReadLine(); // Skip header
            
            while ((line = reader.ReadLine()) != null)
            {
                try
                {
                    var parts = CsvParser.ParseLine(line);
                    if (parts.Length >= 4)
                    {
                        var gameName = parts[0];
                        var startTime = DateTime.Parse(parts[1]);
                        var endTime = DateTime.Parse(parts[2]);
                        var durationMinutes = long.Parse(parts[3]);
                        
                        if (!gameData.TryGetValue(gameName, out var gameItem))
                        {
                            gameItem = new GameItem { GameName = gameName };
                            gameData[gameName] = gameItem;
                        }
                        
                        gameItem.Sessions.Add(new GameSession
                        {
                            StartTime = startTime,
                            EndTime = endTime,
                            DurationMinutes = durationMinutes
                        });
                    }
                }
                catch
                {
                    // Skip malformed lines
                }
            }
            
            return gameData.Values.ToList();
        }

        public static List<GameItem> ReadFromJson(string jsonFile)
        {
            var json = File.ReadAllText(jsonFile);
            var root = JsonSerializer.Deserialize<Dictionary<string, object>>(json);
            if (root == null || !root.TryGetValue("games", out var node) || node == null)
            {
                return new List<GameItem>();
            }
            return JsonSerializer.Deserialize<List<GameItem>>(node.ToString() ?? string.Empty) ?? new List<GameItem>();
        }
    }
}