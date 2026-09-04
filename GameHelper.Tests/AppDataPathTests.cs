using System;
using System.IO;
using GameHelper.Core.Utilities;
using GameHelper.Infrastructure.Providers;
using Xunit;

namespace GameHelper.Tests
{
    [Collection("AppDataPathSequential")]
    public sealed class AppDataPathTests
    {
        [Fact]
        public void GetBaseDirectory_PrefersXdgConfigHomeOverAppData()
        {
            var originalXdg = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
            var originalAppData = Environment.GetEnvironmentVariable("APPDATA");
            var xdg = Path.Combine(Path.GetTempPath(), "gh-tests-xdg", Guid.NewGuid().ToString("N"));
            var appData = Path.Combine(Path.GetTempPath(), "gh-tests-appdata", Guid.NewGuid().ToString("N"));

            try
            {
                Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", xdg);
                Environment.SetEnvironmentVariable("APPDATA", appData);

                Assert.Equal(xdg, AppDataPath.GetBaseDirectory());
            }
            finally
            {
                Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", originalXdg);
                Environment.SetEnvironmentVariable("APPDATA", originalAppData);
            }
        }

        [Fact]
        public void GetBaseDirectory_PrefersDataDirOverrideOverEverything()
        {
            var originalDataDir = Environment.GetEnvironmentVariable(AppDataPath.DataDirectoryEnvironmentVariable);
            var originalXdg = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
            var originalAppData = Environment.GetEnvironmentVariable("APPDATA");
            var dataDir = Path.Combine(Path.GetTempPath(), "gh-tests-datadir", Guid.NewGuid().ToString("N"));

            try
            {
                Environment.SetEnvironmentVariable(AppDataPath.DataDirectoryEnvironmentVariable, dataDir);
                Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", Path.Combine(dataDir, "xdg"));
                Environment.SetEnvironmentVariable("APPDATA", Path.Combine(dataDir, "appdata"));

                Assert.Equal(dataDir, AppDataPath.GetBaseDirectory());
                Assert.Equal(Path.Combine(dataDir, "GameHelper"), AppDataPath.GetGameHelperDirectory());
                Assert.Equal(Path.Combine(dataDir, "GameHelper", "config.yml"), AppDataPath.GetConfigPath());
                Assert.Equal(Path.Combine(dataDir, "GameHelper", "playtime.csv"), AppDataPath.GetPlaytimeCsvPath());
            }
            finally
            {
                Environment.SetEnvironmentVariable(AppDataPath.DataDirectoryEnvironmentVariable, originalDataDir);
                Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", originalXdg);
                Environment.SetEnvironmentVariable("APPDATA", originalAppData);
            }
        }

        [Fact]
        public void GetBaseDirectory_UsesAppDataWhenXdgMissing()
        {
            var originalXdg = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
            var originalAppData = Environment.GetEnvironmentVariable("APPDATA");
            var appData = Path.Combine(Path.GetTempPath(), "gh-tests-appdata", Guid.NewGuid().ToString("N"));

            try
            {
                Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", null);
                Environment.SetEnvironmentVariable("APPDATA", appData);

                Assert.Equal(appData, AppDataPath.GetBaseDirectory());
            }
            finally
            {
                Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", originalXdg);
                Environment.SetEnvironmentVariable("APPDATA", originalAppData);
            }
        }

        [Fact]
        public void GetBaseDirectory_FallsBackToSpecialFolder()
        {
            var originalXdg = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
            var originalAppData = Environment.GetEnvironmentVariable("APPDATA");

            try
            {
                Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", null);
                Environment.SetEnvironmentVariable("APPDATA", null);

                var expected = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
                Assert.Equal(expected, AppDataPath.GetBaseDirectory());
            }
            finally
            {
                Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", originalXdg);
                Environment.SetEnvironmentVariable("APPDATA", originalAppData);
            }
        }

        [Fact]
        public void DefaultProviders_RespectAppDataOverride()
        {
            var originalXdg = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
            var originalAppData = Environment.GetEnvironmentVariable("APPDATA");
            var appData = Path.Combine(Path.GetTempPath(), "gh-tests-appdata", Guid.NewGuid().ToString("N"));

            try
            {
                Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", null);
                Environment.SetEnvironmentVariable("APPDATA", appData);

                var expectedDir = Path.Combine(appData, "GameHelper");
                var expectedConfig = Path.Combine(expectedDir, "config.yml");

                var yaml = new YamlConfigProvider();
                var json = new JsonConfigProvider();

                Directory.CreateDirectory(expectedDir);
                File.WriteAllText(expectedConfig, "games: []");
                Assert.Equal(expectedConfig, yaml.ConfigPath);
                Assert.Equal(Path.Combine(expectedDir, "config.json"), json.ConfigPath);
            }
            finally
            {
                Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", originalXdg);
                Environment.SetEnvironmentVariable("APPDATA", originalAppData);
            }
        }
    }

    [CollectionDefinition("AppDataPathSequential", DisableParallelization = true)]
    public sealed class AppDataPathSequentialCollection : ICollectionFixture<object>
    {
    }
}
