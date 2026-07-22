using System;
using System.Collections.Generic;
using System.IO;
using GameHelper.ConsoleHost.Commands;
using GameHelper.Core.Abstractions;
using GameHelper.Core.Models;
using Moq;
using Xunit;

namespace GameHelper.Tests
{
    public class ConfigCommandTests
    {
        private readonly Mock<IGameConfiguration> _mockGameConfiguration;
        private readonly IGameCatalogService _gameCatalogService;
        private readonly StringWriter _consoleOutput;
        private List<GameConfig> _configData;

        public ConfigCommandTests()
        {
            _configData = new List<GameConfig>();
            _mockGameConfiguration = new Mock<IGameConfiguration>();
            _mockGameConfiguration.Setup(p => p.Read()).Returns(() => new AppConfig { Games = _configData.ToList() });
            _mockGameConfiguration.Setup(p => p.Change(It.IsAny<Action<AppConfig>>()))
                .Returns((Action<AppConfig> change) =>
                {
                    var appConfig = new AppConfig { Games = _configData.ToList() };
                    change(appConfig);
                    _configData = appConfig.Games ?? new List<GameConfig>();
                    return appConfig;
                });
            _gameCatalogService = new GameHelper.Core.Services.GameCatalogService(_mockGameConfiguration.Object);

            _consoleOutput = new StringWriter();
            Console.SetOut(_consoleOutput);
        }

        [Fact]
        public void Add_WithValidName_SavesToConfig()
        {
            var gameName = "test.exe";
            ConfigCommand.Run(_gameCatalogService, new[] { "add", gameName });

            var cfg = Assert.Single(_configData);
            Assert.Equal("test", cfg.DataKey);
            Assert.Equal(gameName, cfg.ExecutableName);
            Assert.Equal(gameName, cfg.Executable);
            Assert.False(cfg.HdrEnabled);
            Assert.Equal($"Added {gameName}.", _consoleOutput.ToString().Trim());
            _mockGameConfiguration.Verify(p => p.Change(It.IsAny<Action<AppConfig>>()), Times.Once);
        }

        [Fact]
        public void Add_WithExecutablePath_SavesPathAndExecutableName()
        {
            var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            var exePath = Path.Combine(tempDir, "PathGame.exe");
            File.WriteAllText(exePath, string.Empty);

            try
            {
                ConfigCommand.Run(_gameCatalogService, new[] { "add", exePath });

                var cfg = Assert.Single(_configData);
                Assert.Equal("pathgame", cfg.DataKey);
                Assert.Equal("PathGame.exe", cfg.ExecutableName);
                Assert.Equal(exePath, cfg.ExecutablePath);
                Assert.Equal(exePath, cfg.Executable);
                Assert.Equal("PathGame", cfg.DisplayName);
                Assert.True(cfg.IsEnabled);
                Assert.False(cfg.HdrEnabled);
                Assert.Contains("Added PathGame.exe.", _consoleOutput.ToString());
                _mockGameConfiguration.Verify(p => p.Change(It.IsAny<Action<AppConfig>>()), Times.Once);
            }
            finally
            {
                Directory.Delete(tempDir, true);
            }
        }

        [Fact]
        public void Add_ExistingName_PreservesDisplayNameAndHdrPreference()
        {
            _configData.Add(new GameConfig
            {
                DataKey = "test",
                Executable = "test.exe",
                DisplayName = "My Test Game",
                IsEnabled = false,
                HdrEnabled = true
            });

            ConfigCommand.Run(_gameCatalogService, new[] { "add", "test.exe" });

            var cfg = Assert.Single(_configData);
            Assert.Equal("My Test Game", cfg.DisplayName);
            Assert.True(cfg.HdrEnabled);
            Assert.True(cfg.IsEnabled);
            Assert.Equal("Updated test.exe.", _consoleOutput.ToString().Trim());
        }

        [Fact]
        public void Add_ExistingPath_PreservesStableDataKey()
        {
            var executablePath = Path.Combine(Path.GetTempPath(), "StableKeyGame.exe");
            _configData.Add(new GameConfig
            {
                DataKey = "my-stable-key",
                Executable = executablePath,
                DisplayName = "Stable Key Game"
            });

            ConfigCommand.Run(_gameCatalogService, new[] { "add", executablePath });

            var cfg = Assert.Single(_configData);
            Assert.Equal("my-stable-key", cfg.DataKey);
            Assert.Contains("Updated StableKeyGame.exe.", _consoleOutput.ToString());
        }

        [Theory]
        [InlineData("")]
        [InlineData(" ")]
        [InlineData(null)]
        public void Add_WithInvalidName_DoesNotSaveAndPrintsError(string? gameName)
        {
            ConfigCommand.Run(_gameCatalogService, new[] { "add", gameName! });

            Assert.Empty(_configData);
            Assert.Equal("Game name cannot be empty.", _consoleOutput.ToString().Trim());
            _mockGameConfiguration.Verify(p => p.Change(It.IsAny<Action<AppConfig>>()), Times.Never);
        }

        [Fact]
        public void Remove_ExistingGame_SavesToConfig()
        {
            var gameName = "test.exe";
            _configData.Add(new GameConfig { DataKey = gameName, Executable = gameName });

            ConfigCommand.Run(_gameCatalogService, new[] { "remove", gameName });

            Assert.DoesNotContain(_configData, config => config.DataKey == gameName);
            Assert.Equal($"Removed {gameName}.", _consoleOutput.ToString().Trim());
            _mockGameConfiguration.Verify(p => p.Change(It.IsAny<Action<AppConfig>>()), Times.Once);
        }

        [Fact]
        public void List_WithGames_PrintsGames()
        {
            _configData.Add(new GameConfig { DataKey = "a.exe", Executable = "a.exe", DisplayName = "Game A", IsEnabled = true, HdrEnabled = true });
            _configData.Add(new GameConfig { DataKey = "b.exe", Executable = "b.exe", IsEnabled = false, HdrEnabled = false });

            ConfigCommand.Run(_gameCatalogService, new[] { "list" });

            var output = _consoleOutput.ToString();
            Assert.Contains("a.exe  DisplayName=Game A  Enabled=True  HDR=True", output);
            Assert.Contains("b.exe  DisplayName=-  Enabled=False  HDR=False", output);
        }

        [Fact]
        public void ImportSteam_AddsAllResolvedSteamGamesInOneBatch()
        {
            var steamResolver = new Mock<ISteamGameResolver>();
            steamResolver.Setup(resolver => resolver.TryEnumerateInstalledGames())
                .Returns(new[]
                {
                    new SteamInstalledGame
                    {
                        AppId = "10",
                        Name = "Steam Adventure",
                        ExecutablePath = @"C:\\SteamLibrary\\Steam Adventure\\adventure.exe"
                    },
                    new SteamInstalledGame
                    {
                        AppId = "20",
                        Name = "Steam Puzzle",
                        ExecutablePath = @"D:\\SteamLibrary\\Steam Puzzle\\puzzle.exe"
                    }
                });

            ConfigCommand.Run(_gameCatalogService, steamResolver.Object, new[] { "import-steam" });

            Assert.Equal(2, _configData.Count);
            Assert.All(_configData, game => Assert.True(game.IsEnabled));
            Assert.Contains(_configData, game => game.DisplayName == "Steam Adventure");
            Assert.Contains(_configData, game => game.DisplayName == "Steam Puzzle");
            Assert.Equal("Steam import completed: Added=2, Updated=0.", _consoleOutput.ToString().Trim());
            _mockGameConfiguration.Verify(p => p.Change(It.IsAny<Action<AppConfig>>()), Times.Once);
        }
    }
}
