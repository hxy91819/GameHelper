using GameHelper.Core.Abstractions;
using GameHelper.Core.Models;
using Microsoft.Extensions.DependencyInjection;
using Spectre.Console;

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
        var console = services.GetService<IAnsiConsole>() ?? AnsiConsole.Console;
        var sub = args[0].ToLowerInvariant();

        switch (sub)
        {
            case "list":
                ListGames(gameCatalogService, console);
                break;
            case "add":
                AddGame(args, gameCatalogService, console);
                break;
            case "remove":
                RemoveGame(args, gameCatalogService, console);
                break;
            default:
                CommandHelpers.PrintUsage();
                break;
        }
    }

    private static void ListGames(IGameCatalogService gameCatalogService, IAnsiConsole console)
    {
        var games = gameCatalogService.GetAll();
        if (games.Count == 0)
        {
            console.MarkupLine("[yellow]No games configured.[/]");
            return;
        }

        var table = new Table();
        table.Border(TableBorder.Rounded);
        table.AddColumn("DataKey");
        table.AddColumn("Enabled");
        table.AddColumn("HDR");

        foreach (var game in games)
        {
            var enabled = game.IsEnabled ? "[green]True[/]" : "[red]False[/]";
            var hdr = game.HdrEnabled ? "[green]True[/]" : "[grey]False[/]";
            table.AddRow(Markup.Escape(game.DataKey), enabled, hdr);
        }

        console.Write(table);
    }

    private static void AddGame(string[] args, IGameCatalogService gameCatalogService, IAnsiConsole console)
    {
        if (args.Length < 2)
        {
            console.MarkupLine("[red]Missing <exe>.[/]");
            return;
        }

        var executableName = args[1];
        if (string.IsNullOrWhiteSpace(executableName))
        {
            console.MarkupLine("[red]Game name cannot be empty.[/]");
            return;
        }

        gameCatalogService.Add(new GameEntryUpsertRequest
        {
            ExecutableName = executableName,
            IsEnabled = true,
            HdrEnabled = false
        });

        console.MarkupLine($"[green]Added {Markup.Escape(executableName)}.[/]");
    }

    private static void RemoveGame(string[] args, IGameCatalogService gameCatalogService, IAnsiConsole console)
    {
        if (args.Length < 2)
        {
            console.MarkupLine("[red]Missing <dataKey>.[/]");
            return;
        }

        var dataKey = args[1];
        if (gameCatalogService.Delete(dataKey))
        {
            console.MarkupLine($"[green]Removed {Markup.Escape(dataKey)}.[/]");
        }
        else
        {
            console.MarkupLine($"[red]Not found: {Markup.Escape(dataKey)}[/]");
        }
    }
}
