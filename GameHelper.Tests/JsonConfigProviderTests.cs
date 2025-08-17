using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CoreModels = GameHelper.Core.Models;
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
        public void Save_Then_Load_Roundtrip_PreservesEntries()
        {
            var provider = new JsonConfigProvider(_configPath);
            var input = new Dictionary<string, CoreModels.GameConfig>(StringComparer.OrdinalIgnoreCase)
            {
                ["cyberpunk2077.exe"] = new CoreModels.GameConfig { Name = "cyberpunk2077.exe", IsEnabled = true, HDREnabled = true },
                ["rdr2.exe"] = new CoreModels.GameConfig { Name = "rdr2.exe", IsEnabled = false, HDREnabled = false },
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
        public void Load_Legacy_ArrayOfStrings_UpgradesToEnabledConfigs()
        {
            var legacy = "{\n  \"games\": [\n    \"witcher3.exe\",\n    \"forza_horizon_5.exe\"\n  ]\n}";
            File.WriteAllText(_configPath, legacy);

            var provider = new JsonConfigProvider(_configPath);
            var map = provider.Load();

            Assert.True(map.ContainsKey("witcher3.exe"));
            Assert.True(map.ContainsKey("FORZA_HORIZON_5.EXE"));

            Assert.All(map.Values, cfg =>
            {
                Assert.True(cfg.IsEnabled);
                Assert.True(cfg.HDREnabled);
                Assert.False(string.IsNullOrWhiteSpace(cfg.Name));
            });
        }

        [Fact]
        public void Load_OnMalformedJson_ReturnsEmpty_NotThrow()
        {
            File.WriteAllText(_configPath, "{ not-json }");
            var provider = new JsonConfigProvider(_configPath);
            var map = provider.Load();
            Assert.Empty(map);
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
