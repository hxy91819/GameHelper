using GameHelper.Infrastructure.Resolvers;

namespace GameHelper.Tests;

public sealed class SteamGameResolverTests
{
    private static readonly object SteamRootEnvironmentLock = new();

    [Fact]
    public void TryEnumerateInstalledGames_ReturnsInstalledGamesFromAllLibraries()
    {
        lock (SteamRootEnvironmentLock)
        {
            var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
            var extraLibrary = Path.Combine(root, "ExtraLibrary");
            var previousSteamRoot = Environment.GetEnvironmentVariable("GAMEHELPER_STEAM_ROOT");

            try
            {
                CreateInstalledGame(root, "101", "Alpha Game", "Alpha", "AlphaGame.exe");
                CreateInstalledGame(extraLibrary, "202", "Beta Game", "Beta", "BetaGame.exe");
                CreateInstalledGame(root, "303", "Missing Executable", "Missing", null);

                var steamApps = Path.Combine(root, "steamapps");
                Directory.CreateDirectory(steamApps);
                File.WriteAllText(
                    Path.Combine(steamApps, "libraryfolders.vdf"),
                    $"\"libraryfolders\"\n{{\n\t\"1\"\n\t{{\n\t\t\"path\"\t\"{extraLibrary.Replace("\\", "\\\\")}\"\n\t}}\n}}");
                Environment.SetEnvironmentVariable("GAMEHELPER_STEAM_ROOT", root);

                var games = new SteamGameResolver().TryEnumerateInstalledGames();

                Assert.Collection(
                    games,
                    game =>
                    {
                        Assert.Equal("101", game.AppId);
                        Assert.Equal("Alpha Game", game.Name);
                        Assert.EndsWith("AlphaGame.exe", game.ExecutablePath, StringComparison.OrdinalIgnoreCase);
                    },
                    game =>
                    {
                        Assert.Equal("202", game.AppId);
                        Assert.Equal("Beta Game", game.Name);
                        Assert.EndsWith("BetaGame.exe", game.ExecutablePath, StringComparison.OrdinalIgnoreCase);
                    });
            }
            finally
            {
                Environment.SetEnvironmentVariable("GAMEHELPER_STEAM_ROOT", previousSteamRoot);
                if (Directory.Exists(root))
                {
                    Directory.Delete(root, recursive: true);
                }
            }
        }
    }

    private static void CreateInstalledGame(
        string library,
        string appId,
        string name,
        string installDirectory,
        string? executableName)
    {
        var steamApps = Path.Combine(library, "steamapps");
        Directory.CreateDirectory(steamApps);
        File.WriteAllText(
            Path.Combine(steamApps, $"appmanifest_{appId}.acf"),
            $"\"AppState\"\n{{\n\t\"name\"\t\"{name}\"\n\t\"installdir\"\t\"{installDirectory}\"\n}}");

        if (executableName is not null)
        {
            var installPath = Path.Combine(steamApps, "common", installDirectory);
            Directory.CreateDirectory(installPath);
            File.WriteAllText(Path.Combine(installPath, executableName), string.Empty);
        }
    }
}
