using System;
using System.Collections.Generic;
using System.IO;
using GameHelper.Core.Models;
using GameHelper.Infrastructure.Providers;
using Xunit;

namespace GameHelper.Tests
{
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
            var map = provider.Load();
            Assert.NotNull(map);
            Assert.Empty(map);
        }

        [Fact]
        public void Save_Then_Load_Roundtrip_PreservesEntriesAndEntryId()
        {
            var provider = new JsonConfigProvider(_configPath);
            var input = new Dictionary<string, GameConfig>(StringComparer.OrdinalIgnoreCase)
            {
                ["a"] = new GameConfig
                {
                    DataKey = "cyberpunk2077",
                    ExecutableName = "cyberpunk2077.exe",
                    IsEnabled = true,
                    HDREnabled = true
                },
                ["b"] = new GameConfig
                {
                    DataKey = "rdr2",
                    ExecutableName = "rdr2.exe",
                    IsEnabled = false,
                    HDREnabled = false
                }
            };

            provider.Save(input);
            var output = provider.Load();
            Assert.Equal(2, output.Count);
            Assert.All(output, kv => Assert.Equal(kv.Key, kv.Value.EntryId, ignoreCase: true));
            Assert.Contains(output.Values, v => v.DataKey == "cyberpunk2077");
            Assert.Contains(output.Values, v => v.DataKey == "rdr2");
        }

        [Fact]
        public void Load_Legacy_ArrayOfStrings_UpgradesToEnabledConfigs()
        {
            var legacy = "{\n  \"games\": [\n    \"witcher3.exe\",\n    \"forza_horizon_5.exe\"\n  ]\n}";
            File.WriteAllText(_configPath, legacy);

            var provider = new JsonConfigProvider(_configPath);
            var map = provider.Load();

            Assert.Equal(2, map.Count);
            Assert.All(map.Values, cfg =>
            {
                Assert.True(cfg.IsEnabled);
                Assert.False(cfg.HDREnabled);
                Assert.False(string.IsNullOrWhiteSpace(cfg.EntryId));
                Assert.False(string.IsNullOrWhiteSpace(cfg.DataKey));
            });
        }

        [Fact]
        public void Load_NewFormatMissingDataKeyAndFallback_ThrowsInvalidDataException()
        {
            var payload = "{\n  \"games\": [\n    { \"displayName\": \"\" }\n  ]\n}";
            File.WriteAllText(_configPath, payload);
            var provider = new JsonConfigProvider(_configPath);

            var exception = Assert.Throws<InvalidDataException>(() => provider.Load());
            Assert.Contains("DataKey", exception.Message, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void Save_WhenDataKeyMissing_ThrowsInvalidDataException()
        {
            var provider = new JsonConfigProvider(_configPath);
            var input = new Dictionary<string, GameConfig>(StringComparer.OrdinalIgnoreCase)
            {
                ["sample.exe"] = new GameConfig
                {
                    ExecutableName = "sample.exe"
                }
            };

            var exception = Assert.Throws<InvalidDataException>(() => provider.Save(input));
            Assert.Contains("DataKey", exception.Message, StringComparison.OrdinalIgnoreCase);
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
