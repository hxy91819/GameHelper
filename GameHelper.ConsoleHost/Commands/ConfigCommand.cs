using GameHelper.Core.Abstractions;
using GameHelper.Core.Models;
using Microsoft.Extensions.DependencyInjection;

namespace GameHelper.ConsoleHost.Commands;

public static class ConfigCommand
{
    public static void Run(IServiceProvider services, string[] args)
    {
        if (args.Length == 0)
        {
            CommandHelpers.PrintUsage();
            return;
        }

        using var scope = services.CreateScope();
        var gameCatalogService = scope.ServiceProvider.GetRequiredService<IGameCatalogService>();
        var sub = args[0].ToLowerInvariant();

        switch (sub)
        {
            case "list":
                ListGames(gameCatalogService);
                break;
            case "add":
                AddGame(args, gameCatalogService);
                break;
            case "remove":
                RemoveGame(args, gameCatalogService);
                break;
            default:
                CommandHelpers.PrintUsage();
                break;
        }
    }

    private static void ListGames(IGameCatalogService gameCatalogService)
    {
        var games = gameCatalogService.GetAll();
        if (games.Count == 0)
        {
            Console.WriteLine("No games configured.");
            return;
        }

        foreach (var game in games)
        {
            Console.WriteLine($"{game.DataKey}  Enabled={game.IsEnabled}  HDR={game.HdrEnabled}");
        }
    }

    private static void AddGame(string[] args, IGameCatalogService gameCatalogService)
    {
        if (args.Length < 2)
        {
            Console.WriteLine("Missing <exe>.");
            return;
        }

        var executableName = args[1];
        if (string.IsNullOrWhiteSpace(executableName))
        {
            Console.WriteLine("Game name cannot be empty.");
            return;
        }

        gameCatalogService.Add(new GameEntryUpsertRequest
        {
            ExecutableName = executableName,
            IsEnabled = true,
            HdrEnabled = false
        });

        Console.WriteLine($"Added {executableName}.");
    }

    private static void RemoveGame(string[] args, IGameCatalogService gameCatalogService)
    {
        if (args.Length < 2)
        {
            Console.WriteLine("Missing <exe>.");
            return;
        }

        var remove = args[1];
        if (gameCatalogService.Delete(remove))
        {
            Console.WriteLine($"Removed {remove}.");
        }
        else
        {
            Console.WriteLine($"Not found: {remove}");
        }
    }
}
