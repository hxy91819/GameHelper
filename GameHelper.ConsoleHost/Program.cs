using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using GameHelper.Core.Abstractions;
using GameHelper.Core.Models;
using GameHelper.Core.Services;
using GameHelper.ConsoleHost;
using GameHelper.Infrastructure.Controllers;
using GameHelper.Infrastructure.Processes;
using GameHelper.Infrastructure.Providers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

var host = Host.CreateDefaultBuilder(args)
    .ConfigureLogging(logging =>
    {
        logging.ClearProviders();
        logging.AddConsole();
    })
    .ConfigureServices(services =>
    {
        // Register core abstractions with infrastructure implementations
        services.AddSingleton<IProcessMonitor, WmiProcessMonitor>();
        services.AddSingleton<IHdrController, NoOpHdrController>();
        services.AddSingleton<IConfigProvider, JsonConfigProvider>();
        services.AddSingleton<IPlayTimeService, FileBackedPlayTimeService>();
        services.AddSingleton<IGameAutomationService, GameAutomationService>();
        services.AddHostedService<Worker>();
    })
    .Build();

var command = args.Length > 0 ? args[0].ToLowerInvariant() : "monitor";
switch (command)
{
    case "monitor":
        await host.RunAsync();
        break;

    case "config":
        RunConfigCommand(host.Services, args.Skip(1).ToArray());
        break;

    case "stats":
        RunStatsCommand(args.Skip(1).ToArray());
        break;

    default:
        PrintUsage();
        break;
}

static void PrintUsage()
{
    Console.WriteLine("GameHelper Console");
    Console.WriteLine("Usage:");
    Console.WriteLine("  monitor");
    Console.WriteLine("  config list");
    Console.WriteLine("  config add <exe>");
    Console.WriteLine("  config remove <exe>");
    Console.WriteLine("  stats [--game <name>]");
}

static void RunConfigCommand(IServiceProvider services, string[] args)
{
    if (args.Length == 0) { PrintUsage(); return; }
    using var scope = services.CreateScope();
    var provider = scope.ServiceProvider.GetRequiredService<IConfigProvider>();
    var sub = args[0].ToLowerInvariant();
    var map = new Dictionary<string, GameConfig>(provider.Load(), StringComparer.OrdinalIgnoreCase);

    switch (sub)
    {
        case "list":
            if (map.Count == 0)
            {
                Console.WriteLine("No games configured.");
                return;
            }
            foreach (var kv in map.OrderBy(k => k.Key, StringComparer.OrdinalIgnoreCase))
            {
                Console.WriteLine($"{kv.Key}  Enabled={kv.Value.IsEnabled}  HDR={kv.Value.HDREnabled}");
            }
            return;

        case "add":
            if (args.Length < 2) { Console.WriteLine("Missing <exe>."); return; }
            var name = args[1];
            map[name] = new GameConfig { Name = name, IsEnabled = true, HDREnabled = true };
            provider.Save(map);
            Console.WriteLine($"Added {name}.");
            return;

        case "remove":
            if (args.Length < 2) { Console.WriteLine("Missing <exe>."); return; }
            var remove = args[1];
            if (map.Remove(remove))
            {
                provider.Save(map);
                Console.WriteLine($"Removed {remove}.");
            }
            else
            {
                Console.WriteLine($"Not found: {remove}");
            }
            return;
        default:
            PrintUsage();
            return;
    }
}

static void RunStatsCommand(string[] args)
{
    string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
    string dir = Path.Combine(appData, "GameHelper");
    string file = Path.Combine(dir, "playtime.json");
    if (!File.Exists(file))
    {
        Console.WriteLine("No playtime data yet.");
        return;
    }

    try
    {
        var json = File.ReadAllText(file);
        var root = JsonSerializer.Deserialize<Dictionary<string, object>>(json);
        if (root == null || !root.TryGetValue("games", out var node) || node == null)
        {
            Console.WriteLine("No playtime data.");
            return;
        }
        var items = JsonSerializer.Deserialize<List<GameItem>>(node.ToString() ?? string.Empty) ?? new();
        string? filterGame = null;
        if (args.Length >= 2 && args[0] == "--game") filterGame = args[1];

        var list = string.IsNullOrWhiteSpace(filterGame)
            ? items
            : items.Where(i => string.Equals(i.GameName, filterGame, StringComparison.OrdinalIgnoreCase)).ToList();

        if (list.Count == 0)
        {
            Console.WriteLine("No matching playtime.");
            return;
        }

        long total = 0;
        foreach (var g in list.OrderBy(g => g.GameName, StringComparer.OrdinalIgnoreCase))
        {
            var minutes = g.Sessions?.Sum(s => s.DurationMinutes) ?? 0;
            total += minutes;
            Console.WriteLine($"{g.GameName}: {minutes} min, sessions={g.Sessions?.Count ?? 0}");
        }
        if (string.IsNullOrWhiteSpace(filterGame))
        {
            Console.WriteLine($"TOTAL: {total} min");
        }
    }
    catch (Exception ex)
    {
        Console.WriteLine($"Failed to read stats: {ex.Message}");
    }
}

// DTOs for JSON mapping used by stats command
internal sealed class GameItem
{
    public string GameName { get; set; } = string.Empty;
    public List<GameSession> Sessions { get; set; } = new();
}

internal sealed class GameSession
{
    public DateTime StartTime { get; set; }
    public DateTime EndTime { get; set; }
    public long DurationMinutes { get; set; }
}
