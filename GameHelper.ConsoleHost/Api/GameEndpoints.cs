using GameHelper.ConsoleHost.Api.Models;
using GameHelper.Core.Abstractions;
using GameHelper.Core.Models;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace GameHelper.ConsoleHost.Api;

public static class GameEndpoints
{
    public static IEndpointRouteBuilder MapGameEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/games");

        group.MapGet("/", (IConfigProvider configProvider) =>
        {
            var configs = configProvider.Load();
            var dtos = configs
                .OrderBy(kv => kv.Value.DisplayName ?? kv.Key, StringComparer.OrdinalIgnoreCase)
                .Select(kv => ToDto(kv.Key, kv.Value))
                .ToList();
            return Results.Ok(dtos);
        });

        group.MapPost("/", (CreateGameRequest request, IConfigProvider configProvider) =>
        {
            if (string.IsNullOrWhiteSpace(request.ExecutableName))
                return Results.BadRequest(new { error = "executableName is required." });

            var configs = new Dictionary<string, GameConfig>(configProvider.Load(), StringComparer.OrdinalIgnoreCase);
            var dataKey = request.ExecutableName;

            var game = new GameConfig
            {
                DataKey = dataKey,
                ExecutableName = request.ExecutableName,
                ExecutablePath = request.ExecutablePath,
                DisplayName = request.DisplayName,
                IsEnabled = request.IsEnabled,
                HDREnabled = request.HdrEnabled
            };

            configs[dataKey] = game;
            configProvider.Save(configs);
            return Results.Created($"/api/games/{dataKey}", ToDto(dataKey, game));
        });

        group.MapPut("/{dataKey}", (string dataKey, UpdateGameRequest request, IConfigProvider configProvider) =>
        {
            var configs = new Dictionary<string, GameConfig>(configProvider.Load(), StringComparer.OrdinalIgnoreCase);
            if (!configs.TryGetValue(dataKey, out var existing))
                return Results.NotFound(new { error = $"Game '{dataKey}' not found." });

            existing.ExecutableName = request.ExecutableName ?? existing.ExecutableName;
            existing.ExecutablePath = request.ExecutablePath ?? existing.ExecutablePath;
            existing.DisplayName = request.DisplayName ?? existing.DisplayName;
            existing.IsEnabled = request.IsEnabled;
            existing.HDREnabled = request.HdrEnabled;

            configs[dataKey] = existing;
            configProvider.Save(configs);
            return Results.Ok(ToDto(dataKey, existing));
        });

        group.MapDelete("/{dataKey}", (string dataKey, IConfigProvider configProvider) =>
        {
            var configs = new Dictionary<string, GameConfig>(configProvider.Load(), StringComparer.OrdinalIgnoreCase);
            if (!configs.Remove(dataKey))
                return Results.NotFound(new { error = $"Game '{dataKey}' not found." });

            configProvider.Save(configs);
            return Results.NoContent();
        });

        return app;
    }

    private static GameDto ToDto(string dictKey, GameConfig config) => new()
    {
        DataKey = dictKey,
        ExecutableName = config.ExecutableName,
        ExecutablePath = config.ExecutablePath,
        DisplayName = config.DisplayName,
        IsEnabled = config.IsEnabled,
        HdrEnabled = config.HDREnabled
    };
}
