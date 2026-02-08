using GameHelper.ConsoleHost.Api.Models;
using GameHelper.ConsoleHost.Models;
using GameHelper.ConsoleHost.Services;
using GameHelper.Core.Abstractions;
using GameHelper.Core.Models;
using GameHelper.Core.Utilities;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace GameHelper.ConsoleHost.Api;

public static class StatsEndpoints
{
    public static IEndpointRouteBuilder MapStatsEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/stats", (IConfigProvider configProvider) =>
        {
            var items = LoadPlaytimeData();
            if (items == null)
                return Results.Ok(Array.Empty<GameStatsDto>());

            var configs = new Dictionary<string, GameConfig>(configProvider.Load(), StringComparer.OrdinalIgnoreCase);
            var cutoff = DateTime.Now.AddDays(-14);

            var dtos = items.Select(g => ToDto(g, configs, cutoff))
                .OrderByDescending(d => d.RecentMinutes)
                .ThenBy(d => d.DisplayName ?? d.GameName, StringComparer.OrdinalIgnoreCase)
                .ToList();

            return Results.Ok(dtos);
        });

        app.MapGet("/api/stats/{gameName}", (string gameName, IConfigProvider configProvider) =>
        {
            var items = LoadPlaytimeData();
            if (items == null)
                return Results.NotFound(new { error = "No playtime data found." });

            var match = items.FirstOrDefault(g =>
                string.Equals(g.GameName, gameName, StringComparison.OrdinalIgnoreCase));

            if (match == null)
                return Results.NotFound(new { error = $"No data for game '{gameName}'." });

            var configs = new Dictionary<string, GameConfig>(configProvider.Load(), StringComparer.OrdinalIgnoreCase);
            var cutoff = DateTime.Now.AddDays(-14);

            return Results.Ok(ToDto(match, configs, cutoff));
        });

        return app;
    }

    private static List<GameItem>? LoadPlaytimeData()
    {
        var csvFile = AppDataPath.GetPlaytimeCsvPath();
        var jsonFile = AppDataPath.GetPlaytimeJsonPath();

        try
        {
            if (File.Exists(csvFile))
                return PlaytimeDataReader.ReadFromCsv(csvFile);
            if (File.Exists(jsonFile))
                return PlaytimeDataReader.ReadFromJson(jsonFile);
        }
        catch
        {
            // ignore read errors
        }

        return null;
    }

    private static GameStatsDto ToDto(GameItem item, Dictionary<string, GameConfig> configs, DateTime cutoff)
    {
        var displayName = configs.TryGetValue(item.GameName, out var gc) && !string.IsNullOrWhiteSpace(gc.DisplayName)
            ? gc.DisplayName
            : null;

        return new GameStatsDto
        {
            GameName = item.GameName,
            DisplayName = displayName,
            TotalMinutes = item.Sessions?.Sum(s => s.DurationMinutes) ?? 0,
            RecentMinutes = item.Sessions?.Where(s => s.EndTime >= cutoff).Sum(s => s.DurationMinutes) ?? 0,
            SessionCount = item.Sessions?.Count ?? 0,
            Sessions = item.Sessions?.Select(s => new SessionDto
            {
                StartTime = s.StartTime,
                EndTime = s.EndTime,
                DurationMinutes = s.DurationMinutes
            }).OrderByDescending(s => s.StartTime).ToList() ?? new List<SessionDto>()
        };
    }
}
