using GameHelper.Core.Models;
using GameHelper.Infrastructure.Providers;

namespace GameHelper.Tests;

public class YamlConfigProviderTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _configPath;

    public YamlConfigProviderTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "GameHelperTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _configPath = Path.Combine(_tempDir, "config.yml");
    }

    [Fact]
    public void LoadAppConfig_WhenFileMissing_ReturnsEmpty()
    {
        var provider = new YamlConfigProvider(_configPath);

        var config = provider.Read();

        Assert.NotNull(config.Games);
        Assert.Empty(config.Games);
        Assert.Equal(ProcessMonitorType.ETW, config.ProcessMonitorType);
    }

    [Fact]
    public void Save_Then_Load_Roundtrip_UsesDataKeyAsStorageKey()
    {
        var provider = new YamlConfigProvider(_configPath);
        var input = new Dictionary<string, GameConfig>(StringComparer.OrdinalIgnoreCase)
        {
            ["ignored-old-key"] = new()
            {
                DataKey = "cyberpunk2077",
                Executable = "cyberpunk2077.exe",
                DisplayName = "Cyberpunk 2077",
                IsEnabled = true,
                HdrEnabled = true
            },
            ["another-old-key"] = new()
            {
                DataKey = "rdr2",
                Executable = "rdr2.exe",
                DisplayName = "Red Dead Redemption 2",
                IsEnabled = false,
                HdrEnabled = false
            }
        };

        SaveGames(provider, input);

        var output = LoadGames(provider);
        Assert.Equal(2, output.Count);
        Assert.Contains("cyberpunk2077", output.Keys);
        Assert.Contains("rdr2", output.Keys);

        var cp = output["cyberpunk2077"];
        Assert.Equal("cyberpunk2077.exe", cp.Executable);
        Assert.Equal("cyberpunk2077.exe", cp.ExecutableName);
        Assert.Equal("Cyberpunk 2077", cp.DisplayName);
        Assert.True(cp.IsEnabled);
        Assert.True(cp.HdrEnabled);
    }

    [Fact]
    public void Load_WhenGameMissingDataKey_SkipsEntry()
    {
        File.WriteAllText(_configPath, """
monitor: ETW
games:
  - executable: broken.exe
    displayName: Broken Entry
""");
        var provider = new YamlConfigProvider(_configPath);

        var output = LoadGames(provider);

        Assert.Empty(output);
    }

    [Fact]
    public void Load_WhenExecutableIdentityIsInvalid_SkipsEntryDuringNormalization()
    {
        File.WriteAllText(_configPath, """
monitor: ETW
games:
  - dataKey: broken
    executable: C:\Games\DirectoryOnly\
""");
        var provider = new YamlConfigProvider(_configPath);

        var output = LoadGames(provider);

        Assert.Empty(output);
    }

    [Fact]
    public void Load_WhenDuplicateDataKey_RepairsWithSuffix()
    {
        File.WriteAllText(_configPath, """
monitor: ETW
games:
  - dataKey: game
    executable: a.exe
  - dataKey: game
    executable: b.exe
""");
        var provider = new YamlConfigProvider(_configPath);

        var output = LoadGames(provider);

        Assert.Equal(2, output.Count);
        Assert.Contains(output.Values, v => v.DataKey == "game");
        Assert.Contains(output.Values, v => v.DataKey == "game2");
    }

    [Fact]
    public void Save_WithExecutablePath_WritesCompactYamlShape()
    {
        var provider = new YamlConfigProvider(_configPath);
        var input = new Dictionary<string, GameConfig>(StringComparer.OrdinalIgnoreCase)
        {
            ["re"] = new()
            {
                DataKey = "re",
                Executable = @"D:\Games\Romantic.Escapades.v2.0.2\game\RE.exe",
                DisplayName = "Romantic Escapades",
                IsEnabled = true,
                HdrEnabled = false
            }
        };

        SaveGames(provider, input);

        var yaml = File.ReadAllText(_configPath);
        Assert.Contains("monitor: ETW", yaml);
        Assert.Contains("startup:", yaml);
        Assert.Contains("dataKey: re", yaml);
        Assert.Contains(@"executable: D:\Games\Romantic.Escapades.v2.0.2\game\RE.exe", yaml);
        Assert.Contains("displayName: Romantic Escapades", yaml);
        Assert.Contains("enabled: true", yaml);
        Assert.Contains("hdr: false", yaml);
        Assert.DoesNotContain("entryId", yaml, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("executableName", yaml, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("executablePath", yaml, StringComparison.OrdinalIgnoreCase);

        var game = Assert.Single(LoadGames(provider).Values);
        Assert.Equal("re", game.DataKey);
        Assert.Equal(@"D:\Games\Romantic.Escapades.v2.0.2\game\RE.exe", game.ExecutablePath);
        Assert.Equal("RE.exe", game.ExecutableName);
        Assert.Equal("Romantic Escapades", game.DisplayName);
    }

    [Fact]
    public void Save_WithDisplayNameContainingColon_WritesParsableYaml()
    {
        var provider = new YamlConfigProvider(_configPath);
        var input = new Dictionary<string, GameConfig>(StringComparer.OrdinalIgnoreCase)
        {
            ["granblue"] = new()
            {
                DataKey = "granblue",
                Executable = "granblue.exe",
                DisplayName = "Granblue Fantasy: Relink",
                IsEnabled = true,
                HdrEnabled = false
            }
        };

        SaveGames(provider, input);

        var yaml = File.ReadAllText(_configPath);
        Assert.Contains("Granblue Fantasy: Relink", yaml);

        var game = Assert.Single(LoadGames(provider).Values);
        Assert.Equal("Granblue Fantasy: Relink", game.DisplayName);
    }

    [Fact]
    public void LoadAppConfig_WhenYamlContainsUnquotedColon_IncludesLocationAndHint()
    {
        File.WriteAllText(_configPath, """
monitor: ETW
games:
  - dataKey: granblue
    executable: granblue.exe
    displayName: Granblue Fantasy: Relink
""");
        var provider = new YamlConfigProvider(_configPath);

        var ex = Assert.Throws<InvalidDataException>(() => provider.Read());

        Assert.Contains(_configPath, ex.Message);
        Assert.Contains("line", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("quote string values", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Save_WhenAppSettingsExist_PreservesGlobalSettings()
    {
        var provider = new YamlConfigProvider(_configPath);
        provider.Change(config =>
        {
            config.ProcessMonitorType = ProcessMonitorType.WMI;
            config.AutoStartInteractiveMonitor = true;
            config.LaunchOnSystemStartup = true;
            config.Games = new List<GameConfig>
            {
                new()
                {
                    DataKey = "old",
                    Executable = "old.exe",
                    IsEnabled = true
                }
            };
        });

        SaveGames(provider, new Dictionary<string, GameConfig>(StringComparer.OrdinalIgnoreCase)
        {
            ["new"] = new()
            {
                DataKey = "new",
                Executable = "new.exe",
                DisplayName = "New Game",
                IsEnabled = true,
                HdrEnabled = true
            }
        });

        var appConfig = provider.Read();
        Assert.Equal(ProcessMonitorType.WMI, appConfig.ProcessMonitorType);
        Assert.True(appConfig.AutoStartInteractiveMonitor);
        Assert.True(appConfig.LaunchOnSystemStartup);

        var game = Assert.Single(appConfig.Games!);
        Assert.Equal("new", game.DataKey);
        Assert.Equal("new.exe", game.Executable);
        Assert.Equal("New Game", game.DisplayName);
        Assert.True(game.HdrEnabled);
    }

    [Fact]
    public void Change_WhenMutationThrows_DoesNotCommitPartialDocument()
    {
        var provider = new YamlConfigProvider(_configPath);
        provider.Change(config => config.AutoStartInteractiveMonitor = true);

        Assert.Throws<InvalidOperationException>(() => provider.Change(config =>
        {
            config.AutoStartInteractiveMonitor = false;
            config.LaunchOnSystemStartup = true;
            throw new InvalidOperationException("abort");
        }));

        var snapshot = provider.Read();
        Assert.True(snapshot.AutoStartInteractiveMonitor);
        Assert.False(snapshot.LaunchOnSystemStartup);
    }

    [Fact]
    public void Change_ConcurrentIndependentChanges_PreserveBothResults()
    {
        var provider = new YamlConfigProvider(_configPath);

        Parallel.Invoke(
            () => provider.Change(config => config.AutoStartInteractiveMonitor = true),
            () => provider.Change(config => config.LaunchOnSystemStartup = true));

        var snapshot = provider.Read();
        Assert.True(snapshot.AutoStartInteractiveMonitor);
        Assert.True(snapshot.LaunchOnSystemStartup);
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

    private static IReadOnlyDictionary<string, GameConfig> LoadGames(YamlConfigProvider provider) =>
        provider.Read().Games.ToDictionary(game => game.DataKey, StringComparer.OrdinalIgnoreCase);

    private static void SaveGames(
        YamlConfigProvider provider,
        IReadOnlyDictionary<string, GameConfig> games) =>
        provider.Change(config => config.Games = games.Values.ToList());
}
