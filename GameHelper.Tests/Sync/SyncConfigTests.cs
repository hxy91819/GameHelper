using GameHelper.Core.Models;
using GameHelper.Infrastructure.Providers;

namespace GameHelper.Tests.Sync;

public class SyncConfigTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _configPath;

    public SyncConfigTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "GameHelperTests_SyncConfig", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _configPath = Path.Combine(_tempDir, "config.yml");
    }

    [Fact]
    public void Load_WithSyncSection_MapsAllFields()
    {
        File.WriteAllText(_configPath, """
monitor: ETW
sync:
  enabled: true
  provider: github
  method: api
  repo: mason/game-stats
  branch: main
  token: ghp_test
  directory: stats
  intervalMinutes: 120
  includeRawCsv: true
games: []
""");

        var sync = new YamlConfigProvider(_configPath).Read().SyncSettings;

        Assert.NotNull(sync);
        Assert.True(sync!.Enabled);
        Assert.Equal("github", sync.Provider);
        Assert.Equal("api", sync.Method);
        Assert.Equal("mason/game-stats", sync.Repo);
        Assert.Equal("main", sync.Branch);
        Assert.Equal("ghp_test", sync.Token);
        Assert.Equal("stats", sync.Directory);
        Assert.Equal(120, sync.IntervalMinutes);
        Assert.True(sync.IncludeRawCsv);
    }

    [Fact]
    public void Load_WithoutSyncSection_ReturnsNull()
    {
        File.WriteAllText(_configPath, "monitor: ETW\ngames: []\n");

        var sync = new YamlConfigProvider(_configPath).Read().SyncSettings;

        Assert.Null(sync);
    }

    [Fact]
    public void Load_WithUnknownSyncFields_IgnoresThem()
    {
        File.WriteAllText(_configPath, """
sync:
  enabled: true
  repo: mason/game-stats
  futureField: whatever
""");

        var sync = new YamlConfigProvider(_configPath).Read().SyncSettings;

        Assert.NotNull(sync);
        Assert.True(sync!.Enabled);
        Assert.Equal("mason/game-stats", sync.Repo);
    }

    [Fact]
    public void Save_RoundTripsSyncSettings()
    {
        var provider = new YamlConfigProvider(_configPath);
        provider.Change(config =>
        {
            config.SyncSettings = new SyncSettings
            {
                Enabled = true,
                Method = "git",
                Repo = "Mason/GameStats",
                IntervalMinutes = 30,
                IncludeRawCsv = false
            };
        });

        var yaml = File.ReadAllText(_configPath);
        Assert.Contains("sync:", yaml);
        Assert.Contains("repo: Mason/GameStats", yaml);
        Assert.DoesNotContain("token:", yaml);

        var sync = provider.Read().SyncSettings;
        Assert.NotNull(sync);
        Assert.True(sync!.Enabled);
        Assert.Equal("git", sync.Method);
        Assert.Equal("Mason/GameStats", sync.Repo);
        Assert.Equal(30, sync.IntervalMinutes);
        Assert.False(sync.IncludeRawCsv);
    }

    [Fact]
    public void Save_WithoutSyncSettings_DoesNotWriteSyncSection()
    {
        var provider = new YamlConfigProvider(_configPath);
        provider.Change(config => config.AutoStartInteractiveMonitor = true);

        var yaml = File.ReadAllText(_configPath);
        Assert.DoesNotContain("sync:", yaml);
        Assert.Null(provider.Read().SyncSettings);
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
}
