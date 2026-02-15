using System;
using System.Collections.Generic;
using System.IO;
using GameHelper.ConsoleHost.Commands;
using GameHelper.Core.Abstractions;
using GameHelper.Core.Models;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Spectre.Console;
using Spectre.Console.Testing;
using Xunit;

namespace GameHelper.Tests
{
    public class ConfigCommandTests
    {
        private readonly Mock<IConfigProvider> _mockConfigProvider;
        private readonly IGameCatalogService _gameCatalogService;
        private readonly IServiceProvider _serviceProvider;
        private readonly TestConsole _testConsole;
        private Dictionary<string, GameConfig> _configData;

        public ConfigCommandTests()
        {
            _configData = new Dictionary<string, GameConfig>(StringComparer.OrdinalIgnoreCase);
            _mockConfigProvider = new Mock<IConfigProvider>();
            _mockConfigProvider.Setup(p => p.Load()).Returns(() => _configData);
            _mockConfigProvider.Setup(p => p.Save(It.IsAny<IReadOnlyDictionary<string, GameConfig>>()))
                .Callback<IReadOnlyDictionary<string, GameConfig>>(data => _configData = new Dictionary<string, GameConfig>(data));
            _gameCatalogService = new GameHelper.Core.Services.GameCatalogService(_mockConfigProvider.Object);

            var mockServiceProvider = new Mock<IServiceProvider>();
            var mockScope = new Mock<IServiceScope>();
            var mockScopeFactory = new Mock<IServiceScopeFactory>();

            _testConsole = new TestConsole();

            mockScope.Setup(s => s.ServiceProvider).Returns(mockServiceProvider.Object);
            mockScopeFactory.Setup(f => f.CreateScope()).Returns(mockScope.Object);
            mockServiceProvider.Setup(p => p.GetService(typeof(IServiceScopeFactory))).Returns(mockScopeFactory.Object);
            mockServiceProvider.Setup(p => p.GetService(typeof(IGameCatalogService))).Returns(_gameCatalogService);
            mockServiceProvider.Setup(p => p.GetService(typeof(IAnsiConsole))).Returns(_testConsole);

            _serviceProvider = mockServiceProvider.Object;
        }

        [Fact]
        public void Add_WithValidName_SavesToConfig()
        {
            var gameName = "test.exe";
            ConfigCommand.Run(_serviceProvider, new[] { "add", gameName });

            var cfg = Assert.Single(_configData.Values);
            Assert.Equal(gameName, cfg.DataKey);
            Assert.Equal(gameName, cfg.ExecutableName);
            Assert.False(string.IsNullOrWhiteSpace(cfg.EntryId));
            Assert.False(cfg.HDREnabled);
            Assert.Contains($"Added {gameName}", _testConsole.Output);
            _mockConfigProvider.Verify(p => p.Save(It.IsAny<IReadOnlyDictionary<string, GameConfig>>()), Times.Once);
        }

        [Theory]
        [InlineData("")]
        [InlineData(" ")]
        [InlineData(null)]
        public void Add_WithInvalidName_DoesNotSaveAndPrintsError(string? gameName)
        {
            ConfigCommand.Run(_serviceProvider, new[] { "add", gameName! });

            Assert.Empty(_configData);
            Assert.Contains("Game name cannot be empty", _testConsole.Output);
            _mockConfigProvider.Verify(p => p.Save(It.IsAny<IReadOnlyDictionary<string, GameConfig>>()), Times.Never);
        }

        [Fact]
        public void Remove_ExistingGame_SavesToConfig()
        {
            var gameName = "test.exe";
            _configData[gameName] = new GameConfig { DataKey = gameName, ExecutableName = gameName };

            ConfigCommand.Run(_serviceProvider, new[] { "remove", gameName });

            Assert.DoesNotContain(gameName, _configData.Keys);
            Assert.Contains($"Removed {gameName}", _testConsole.Output);
            _mockConfigProvider.Verify(p => p.Save(It.IsAny<IReadOnlyDictionary<string, GameConfig>>()), Times.Once);
        }

        [Fact]
        public void List_WithGames_PrintsGames()
        {
            _configData["a.exe"] = new GameConfig { DataKey = "a.exe", ExecutableName = "a.exe", IsEnabled = true, HDREnabled = true };
            _configData["b.exe"] = new GameConfig { DataKey = "b.exe", ExecutableName = "b.exe", IsEnabled = false, HDREnabled = false };

            ConfigCommand.Run(_serviceProvider, new[] { "list" });

            var output = _testConsole.Output;
            Assert.Contains("a.exe", output);
            Assert.Contains("b.exe", output);
            Assert.Contains("DataKey", output);
            Assert.Contains("Enabled", output);
            Assert.Contains("HDR", output);
            Assert.Contains("True", output);
            Assert.Contains("False", output);
        }
    }
}
