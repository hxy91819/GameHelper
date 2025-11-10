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
        }

        [Fact]
        public void Save_Then_Load_Roundtrip_PreservesEntries()
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
                },
            };

            provider.Save(input);
            Assert.True(File.Exists(_configPath));

            var output = provider.Load();
            Assert.Equal(2, output.Count);

            var cp = output["CYBERPUNK2077.EXE"]; // case-insensitive
            Assert.Equal("cyberpunk2077", cp.DataKey);
            Assert.Equal("Cyberpunk 2077", cp.DisplayName);
            Assert.True(cp.IsEnabled);
            Assert.True(cp.HDREnabled);

            var rdr2 = output["rdr2.exe"];
            Assert.Equal("rdr2", rdr2.DataKey);
            Assert.Equal("Red Dead Redemption 2", rdr2.DisplayName);
            Assert.False(rdr2.IsEnabled);
            Assert.False(rdr2.HDREnabled);
        }

        [Fact]
        public void Load_WhenGameMissingDataKeyAndExecutableName_Throws()
        {
            var yaml = "games:\n  - displayName: Broken Entry\n";
            File.WriteAllText(_configPath, yaml);
            var provider = new YamlConfigProvider(_configPath);

            var exception = Assert.Throws<InvalidDataException>(() => provider.Load());
            Assert.Contains("配置项缺少必填字段 DataKey", exception.Message);
        }

        [Fact]
        public void Load_WhenGameMissingDataKeyButHasExecutableName_Throws()
        {
            var yaml = "games:\n  - executableName: sample.exe\n";
            File.WriteAllText(_configPath, yaml);
            var provider = new YamlConfigProvider(_configPath);

            var exception = Assert.Throws<InvalidDataException>(() => provider.Load());
            Assert.Contains("配置项缺少必填字段 DataKey", exception.Message);
        }

        [Fact]
        public void LoadAppConfig_OnMalformedYaml_ThrowsInvalidDataException()
        {
            File.WriteAllText(_configPath, "games: - name: unclosed_string: '");
            var provider = new YamlConfigProvider(_configPath);
            var exception = Assert.Throws<InvalidDataException>(() => provider.LoadAppConfig());
            Assert.Contains("Failed to deserialize config file", exception.Message);
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