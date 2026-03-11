using System;
using System.Collections.Generic;
using System.IO;
using GameHelper.Core.Models;
using GameHelper.Infrastructure.Providers;
using Xunit;

namespace GameHelper.Tests
{
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
            var config = provider.LoadAppConfig();
            Assert.NotNull(config);
            Assert.NotNull(config.Games);
            Assert.Empty(config.Games);
            Assert.Equal(ProcessMonitorType.ETW, config.ProcessMonitorType);
        }

        [Fact]
        public void Save_Then_Load_Roundtrip_PreservesEntriesAndAddsEntryId()
        {
            var provider = new YamlConfigProvider(_configPath);
            var input = new Dictionary<string, GameConfig>(StringComparer.OrdinalIgnoreCase)
            {
                ["cyberpunk2077.exe"] = new GameConfig
                {
                    DataKey = "cyberpunk2077",
                    ExecutableName = "cyberpunk2077.exe",
                    DisplayName = "Cyberpunk 2077",
                    IsEnabled = true,
                    HDREnabled = true
                },
                ["rdr2.exe"] = new GameConfig
                {
                    DataKey = "rdr2",
                    ExecutableName = "rdr2.exe",
                    DisplayName = "Red Dead Redemption 2",
                    IsEnabled = false,
                    HDREnabled = false
                }
            };

            provider.Save(input);
            Assert.True(File.Exists(_configPath));

            var output = provider.Load();
            Assert.Equal(2, output.Count);
            Assert.All(output, kv =>
            {
                Assert.False(string.IsNullOrWhiteSpace(kv.Key));
                Assert.Equal(kv.Key, kv.Value.EntryId, ignoreCase: true);
            });

            var cp = Assert.Single(output.Values, v => v.DataKey == "cyberpunk2077");
            Assert.Equal("Cyberpunk 2077", cp.DisplayName);
            Assert.True(cp.IsEnabled);
            Assert.True(cp.HDREnabled);
        }

        [Fact]
        public void Load_WhenGameMissingDataKeyAndExecutableName_UsesDisplayName()
        {
            var yaml = "games:\n  - displayName: Broken Entry\n";
            File.WriteAllText(_configPath, yaml);
            var provider = new YamlConfigProvider(_configPath);

            var output = provider.Load();
            var entry = Assert.Single(output.Values);
            Assert.Equal("Broken Entry", entry.DataKey);
            Assert.Equal("Broken Entry", entry.DisplayName);
            Assert.False(string.IsNullOrWhiteSpace(entry.EntryId));
        }

        [Fact]
        public void Load_WhenDuplicateDataKey_RepairsWithSuffix()
        {
            var yaml = """
games:
  - dataKey: game
    executableName: a.exe
  - dataKey: game
    executableName: b.exe
""";
            File.WriteAllText(_configPath, yaml);
            var provider = new YamlConfigProvider(_configPath);

            var output = provider.Load();
            Assert.Equal(2, output.Count);
            Assert.Contains(output.Values, v => v.DataKey == "game");
            Assert.Contains(output.Values, v => v.DataKey == "game2");
        }

        [Fact]
        public void Save_WithExecutablePath_FormatsYamlCorrectly()
        {
            var provider = new YamlConfigProvider(_configPath);
            var input = new Dictionary<string, GameConfig>(StringComparer.OrdinalIgnoreCase)
            {
                ["entry"] = new GameConfig
                {
                    EntryId = "entry",
                    DataKey = "re",
                    ExecutablePath = @"D:\Games\Romantic.Escapades.v2.0.2\game\RE.exe",
                    ExecutableName = "RE.exe",
                    DisplayName = "Romantic Escapades",
                    IsEnabled = true,
                    HDREnabled = false
                }
            };

            provider.Save(input);
            Assert.True(File.Exists(_configPath));

            var output = provider.Load();
            var game = Assert.Single(output.Values);
            Assert.Equal("re", game.DataKey);
            Assert.Equal(@"D:\Games\Romantic.Escapades.v2.0.2\game\RE.exe", game.ExecutablePath);
            Assert.Equal("RE.exe", game.ExecutableName);
            Assert.Equal("Romantic Escapades", game.DisplayName);
        }

        [Fact]
        public void Load_WhenCalledTwice_ReturnsIndependentObjects()
        {
            var yaml = "games:\n  - entryId: id1\n    dataKey: game1\n    executableName: game1.exe";
            File.WriteAllText(_configPath, yaml);
            var provider = new YamlConfigProvider(_configPath);

            var first = provider.Load();
            var second = provider.Load();

            Assert.NotSame(first, second);
            Assert.NotSame(first["id1"], second["id1"]);

            first["id1"].DisplayName = "Modified";
            Assert.Null(second["id1"].DisplayName);
        }

        [Fact]
        public void Load_WhenFileModified_RefreshesConfig()
        {
            var yaml1 = "games:\n  - entryId: id1\n    dataKey: game1\n    executableName: game1.exe";
            File.WriteAllText(_configPath, yaml1);
            var provider = new YamlConfigProvider(_configPath);

            var first = provider.Load();
            Assert.Single(first);
            Assert.Equal("game1", first["id1"].DataKey);

            // Update file and timestamp
            var yaml2 = "games:\n  - entryId: id2\n    dataKey: game2\n    executableName: game2.exe";
            File.WriteAllText(_configPath, yaml2);
            File.SetLastWriteTimeUtc(_configPath, DateTime.UtcNow.AddSeconds(1));

            var second = provider.Load();
            Assert.Single(second);
            Assert.Equal("game2", second["id2"].DataKey);
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
}
