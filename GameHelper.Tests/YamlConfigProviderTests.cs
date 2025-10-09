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
                ["cyberpunk2077.exe"] = new GameConfig { Name = "cyberpunk2077.exe", IsEnabled = true, HDREnabled = true },
                ["rdr2.exe"] = new GameConfig { Name = "rdr2.exe", IsEnabled = false, HDREnabled = false },
            };

            provider.Save(input);
            Assert.True(File.Exists(_configPath));

            var output = provider.Load();
            Assert.Equal(2, output.Count);

            var cp = output["CYBERPUNK2077.EXE"]; // case-insensitive
            Assert.True(cp.IsEnabled);
            Assert.True(cp.HDREnabled);

            var rdr2 = output["rdr2.exe"];
            Assert.False(rdr2.IsEnabled);
            Assert.False(rdr2.HDREnabled);
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