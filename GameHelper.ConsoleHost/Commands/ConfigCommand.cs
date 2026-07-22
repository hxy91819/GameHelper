using GameHelper.Core.Abstractions;
using GameHelper.Core.Models;
using GameHelper.ConsoleHost.Utilities;

namespace GameHelper.ConsoleHost.Commands;

public static class ConfigCommand
{
    public static void Run(IGameCatalogService gameCatalogService, string[] args)
    {
        Run(gameCatalogService, null, args);
    }

    public static void Run(
        IGameCatalogService gameCatalogService,
        ISteamGameResolver? steamGameResolver,
        string[] args)
    {
        ArgumentNullException.ThrowIfNull(gameCatalogService);

        if (args.Length == 0)
        {
            CommandHelpers.PrintUsage();
            return;
        }

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
            case "import-steam":
                ImportSteamGames(gameCatalogService, steamGameResolver);
                break;
            default:
                CommandHelpers.PrintUsage();
                break;
        }
    }

    private static void ImportSteamGames(
        IGameCatalogService gameCatalogService,
        ISteamGameResolver? steamGameResolver)
    {
        if (steamGameResolver is null)
        {
            Console.WriteLine("Steam import is unavailable because the Steam resolver is not configured.");
            return;
        }

        var games = steamGameResolver.TryEnumerateInstalledGames();
        if (games.Count == 0)
        {
            Console.WriteLine("No installed Steam games with a resolvable executable were found.");
            return;
        }

        var results = gameCatalogService.BatchIntake(games.Select(game => new GameCatalogIntakeRequest
        {
            Executable = ExecutableIdentity.Parse(game.ExecutablePath),
            ProductName = game.Name,
            DisplayName = game.Name,
            IsEnabled = true
        }));
        var added = results.Count(result => result.WasAdded);
        Console.WriteLine($"Steam import completed: Added={added}, Updated={results.Count - added}.");
    }

    private static void ListGames(IGameCatalogService gameCatalogService)
    {
        var games = gameCatalogService.List();
        if (games.Count == 0)
        {
            Console.WriteLine("No games configured.");
            return;
        }

        foreach (var game in games)
        {
            var displayName = string.IsNullOrWhiteSpace(game.DisplayName) ? "-" : game.DisplayName;
            Console.WriteLine($"{game.DataKey}  DisplayName={displayName}  Enabled={game.IsEnabled}  HDR={game.HdrEnabled}");
        }
    }

    private static void AddGame(string[] args, IGameCatalogService gameCatalogService)
    {
        if (args.Length < 2)
        {
            Console.WriteLine("Missing <exe>.");
            return;
        }

        var executableInput = args[1];
        if (string.IsNullOrWhiteSpace(executableInput))
        {
            Console.WriteLine("Game name cannot be empty.");
            return;
        }

        var pathImportRequest = TryCreatePathImportRequest(executableInput);
        if (pathImportRequest is not null)
        {
            var result = gameCatalogService.Intake(pathImportRequest);
            var entry = result.Entry;
            Console.WriteLine(result.WasAdded
                ? $"Added {entry.ExecutableName}. DataKey={entry.DataKey} Path={entry.ExecutablePath}"
                : $"Updated {entry.ExecutableName}. DataKey={entry.DataKey} Path={entry.ExecutablePath}");
            return;
        }

        var executableName = executableInput.Trim();
        var nameOnlyResult = gameCatalogService.Intake(new GameCatalogIntakeRequest
        {
            Executable = ExecutableIdentity.Parse(executableName),
            IsEnabled = true
        });

        Console.WriteLine(nameOnlyResult.WasAdded
            ? $"Added {executableName}."
            : $"Updated {executableName}.");
    }

    private static GameCatalogIntakeRequest? TryCreatePathImportRequest(string executableInput)
    {
        var executablePath = executableInput.Trim();
        if (!executablePath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ||
            !LooksLikePath(executablePath))
        {
            return null;
        }

        var executableName = Path.GetFileName(executablePath);
        if (string.IsNullOrWhiteSpace(executableName))
        {
            return null;
        }

        var displayName = Path.GetFileNameWithoutExtension(executableName);
        var (productName, _) = File.Exists(executablePath)
            ? GameMetadataExtractor.ExtractMetadata(executablePath)
            : (null, null);

        return new GameCatalogIntakeRequest
        {
            Executable = ExecutableIdentity.Parse(executablePath),
            DisplayName = displayName,
            ProductName = productName,
            IsEnabled = true
        };
    }

    private static bool LooksLikePath(string value)
    {
        return Path.IsPathFullyQualified(value) ||
               value.Contains(Path.DirectorySeparatorChar) ||
               value.Contains(Path.AltDirectorySeparatorChar);
    }

    private static void RemoveGame(string[] args, IGameCatalogService gameCatalogService)
    {
        if (args.Length < 2)
        {
            Console.WriteLine("Missing <dataKey>.");
            return;
        }

        var dataKey = args[1];
        if (gameCatalogService.Remove(dataKey))
        {
            Console.WriteLine($"Removed {dataKey}.");
        }
        else
        {
            Console.WriteLine($"Not found: {dataKey}");
        }
    }
}
