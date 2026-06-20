using GameHelper.Core.Abstractions;
using GameHelper.Core.Models;
using GameHelper.Core.Utilities;
using GameHelper.ConsoleHost.Utilities;

namespace GameHelper.ConsoleHost.Commands;

public static class ConfigCommand
{
    public static void Run(IGameCatalogService gameCatalogService, string[] args)
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

        var executableInput = args[1];
        if (string.IsNullOrWhiteSpace(executableInput))
        {
            Console.WriteLine("Game name cannot be empty.");
            return;
        }

        var pathImportRequest = TryCreatePathImportRequest(executableInput);
        if (pathImportRequest is not null)
        {
            var result = gameCatalogService.Import(pathImportRequest);
            var entry = result.Entry;
            Console.WriteLine(result.WasAdded
                ? $"Added {entry.ExecutableName}. DataKey={entry.DataKey} Path={entry.ExecutablePath}"
                : $"Updated {entry.ExecutableName}. DataKey={entry.DataKey} Path={entry.ExecutablePath}");
            return;
        }

        var executableName = executableInput.Trim();
        gameCatalogService.Add(new GameEntryUpsertRequest
        {
            ExecutableName = executableName,
            IsEnabled = true,
            HdrEnabled = false
        });

        Console.WriteLine($"Added {executableName}.");
    }

    private static GameEntryImportRequest? TryCreatePathImportRequest(string executableInput)
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

        return new GameEntryImportRequest
        {
            ExecutableName = executableName,
            ExecutablePath = executablePath,
            DisplayName = displayName,
            BaseDataKey = DataKeyGenerator.GenerateBaseDataKey(executablePath, productName),
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
        if (gameCatalogService.Delete(dataKey))
        {
            Console.WriteLine($"Removed {dataKey}.");
        }
        else
        {
            Console.WriteLine($"Not found: {dataKey}");
        }
    }
}
