using GameHelper.Core.Models;
using GameHelper.Infrastructure.Providers;

namespace GameHelper.Tests;

public class JsonConfigProviderTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _configPath;

    public JsonConfigProviderTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "GameHelperTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _configPath = Path.Combine(_tempDir, "config.json");
    }

    [Fact]
    public void Load_WhenFileMissing_ReturnsEmpty()
    {
        var provider = new JsonConfigProvider(_configPath);

        var map = LoadGames(provider);

        Assert.NotNull(map);
        Assert.Empty(map);
    }

    [Fact]
    public void Save_Then_Load_Roundtrip_UsesDataKeyAsStorageKey()
    {
        var provider = new JsonConfigProvider(_configPath);
        var input = new Dictionary<string, GameConfig>(StringComparer.OrdinalIgnoreCase)
        {
            ["a"] = new()
            {
                DataKey = "cyberpunk2077",
                Executable = "cyberpunk2077.exe",
                IsEnabled = true,
                HdrEnabled = true
            },
            ["b"] = new()
            {
                DataKey = "rdr2",
                Executable = "rdr2.exe",
                IsEnabled = false,
                HdrEnabled = false
            }
        };

        SaveGames(provider, input);

        var output = LoadGames(provider);
        Assert.Equal(2, output.Count);
        Assert.Contains("cyberpunk2077", output.Keys);
        Assert.Contains("rdr2", output.Keys);
        Assert.Contains(output.Values, v => v.DataKey == "cyberpunk2077" && v.Executable == "cyberpunk2077.exe");
        Assert.Contains(output.Values, v => v.DataKey == "rdr2" && v.Executable == "rdr2.exe");
    }

    [Fact]
    public void Load_ArrayOfStrings_IsNotSupported()
    {
        File.WriteAllText(_configPath, """
{
  "games": [
    "witcher3.exe",
    "forza_horizon_5.exe"
  ]
}
""");
        var provider = new JsonConfigProvider(_configPath);

        var map = LoadGames(provider);

        Assert.Empty(map);
    }

    [Fact]
    public void Load_NewFormatMissingDataKey_ThrowsInvalidDataException()
    {
        File.WriteAllText(_configPath, """
{
  "games": [
    { "executable": "broken.exe" }
  ]
}
""");
        var provider = new JsonConfigProvider(_configPath);

        var exception = Assert.Throws<InvalidDataException>(() => provider.Read());
        Assert.Contains("DataKey", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Save_WhenDataKeyMissing_ThrowsInvalidDataException()
    {
        var provider = new JsonConfigProvider(_configPath);
        var input = new Dictionary<string, GameConfig>(StringComparer.OrdinalIgnoreCase)
        {
            ["sample.exe"] = new()
            {
                Executable = "sample.exe"
            }
        };

        var exception = Assert.Throws<InvalidDataException>(() => SaveGames(provider, input));
        Assert.Contains("DataKey", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Change_Then_Read_RoundtripsWholeDocumentSettings()
    {
        var provider = new JsonConfigProvider(_configPath);

        provider.Change(config =>
        {
            config.ProcessMonitorType = ProcessMonitorType.WMI;
            config.AutoStartInteractiveMonitor = true;
            config.LaunchOnSystemStartup = true;
            config.Games =
            [
                new GameConfig { DataKey = "game", Executable = "game.exe" }
            ];
        });

        var result = provider.Read();
        Assert.Equal(ProcessMonitorType.WMI, result.ProcessMonitorType);
        Assert.True(result.AutoStartInteractiveMonitor);
        Assert.True(result.LaunchOnSystemStartup);
        Assert.Single(result.Games);
    }

    [Fact]
    public void Read_LegacyExecutableFields_MapsToSingleExecutableIdentity()
    {
        File.WriteAllText(_configPath, """
{
  "games": [
    {
      "dataKey": "path-game",
      "executableName": "path-game.exe",
      "executablePath": "C:\\Games\\PathGame\\path-game.exe",
      "displayName": "Path Game"
    },
    {
      "dataKey": "name-game",
      "executableName": "name-game.exe"
    }
  ]
}
""");
        var provider = new JsonConfigProvider(_configPath);

        var games = provider.Read().Games;

        Assert.Equal(@"C:\Games\PathGame\path-game.exe", games.Single(game => game.DataKey == "path-game").Executable);
        Assert.Equal("name-game.exe", games.Single(game => game.DataKey == "name-game").Executable);
    }

    [Fact]
    public void Read_MalformedTopLevelSetting_PreservesValidGames()
    {
        File.WriteAllText(_configPath, """
{
  "processMonitorType": "WMI",
  "autoStartInteractiveMonitor": "not-a-boolean",
  "games": [
    { "dataKey": "game", "executable": "game.exe" }
  ]
}
""");
        var provider = new JsonConfigProvider(_configPath);

        var result = provider.Read();

        Assert.Single(result.Games);
        Assert.Equal(ProcessMonitorType.WMI, result.ProcessMonitorType);
        Assert.False(result.AutoStartInteractiveMonitor);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDir))
            {
                Directory.Delete(_tempDir, recursive: true);
            }
        }
        catch
        {
            // best-effort cleanup
        }
    }

    private static IReadOnlyDictionary<string, GameConfig> LoadGames(JsonConfigProvider provider) =>
        provider.Read().Games.ToDictionary(game => game.DataKey, StringComparer.OrdinalIgnoreCase);

    private static void SaveGames(
        JsonConfigProvider provider,
        IReadOnlyDictionary<string, GameConfig> games) =>
        provider.Change(config => config.Games = games.Values.ToList());
}
