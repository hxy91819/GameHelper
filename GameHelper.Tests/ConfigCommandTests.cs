using System;
using System.Collections.Generic;
using System.IO;
using GameHelper.ConsoleHost.Commands;
using GameHelper.Core.Abstractions;
using GameHelper.Core.Models;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Xunit;

namespace GameHelper.Tests
{
    public class ConfigCommandTests
    {
        private readonly Mock<IConfigProvider> _mockConfigProvider;
        private readonly IServiceProvider _serviceProvider;
        private readonly StringWriter _consoleOutput;
        private Dictionary<string, GameConfig> _configData;

        public ConfigCommandTests()
        {
            _configData = new Dictionary<string, GameConfig>(StringComparer.OrdinalIgnoreCase);
            _mockConfigProvider = new Mock<IConfigProvider>();
            _mockConfigProvider.Setup(p => p.Load()).Returns(() => _configData);
            _mockConfigProvider.Setup(p => p.Save(It.IsAny<IReadOnlyDictionary<string, GameConfig>>()))
                .Callback<IReadOnlyDictionary<string, GameConfig>>(data => _configData = new Dictionary<string, GameConfig>(data));

            var mockServiceProvider = new Mock<IServiceProvider>();
            var mockScope = new Mock<IServiceScope>();
            var mockScopeFactory = new Mock<IServiceScopeFactory>();

            mockScope.Setup(s => s.ServiceProvider).Returns(mockServiceProvider.Object);
            mockScopeFactory.Setup(f => f.CreateScope()).Returns(mockScope.Object);
            mockServiceProvider.Setup(p => p.GetService(typeof(IServiceScopeFactory))).Returns(mockScopeFactory.Object);
            mockServiceProvider.Setup(p => p.GetService(typeof(IConfigProvider))).Returns(_mockConfigProvider.Object);

            _serviceProvider = mockServiceProvider.Object;

            _consoleOutput = new StringWriter();
            Console.SetOut(_consoleOutput);
        }

        [Fact]
        public void Add_WithValidName_SavesToConfig()
        {
            var gameName = "test.exe";
            ConfigCommand.Run(_serviceProvider, new[] { "add", gameName });

            Assert.Contains(gameName, _configData.Keys);
            Assert.Equal($"Added {gameName}.", _consoleOutput.ToString().Trim());
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
            Assert.Equal("Game name cannot be empty.", _consoleOutput.ToString().Trim());
            _mockConfigProvider.Verify(p => p.Save(It.IsAny<IReadOnlyDictionary<string, GameConfig>>()), Times.Never);
        }

        [Fact]
        public void Remove_ExistingGame_SavesToConfig()
        {
            var gameName = "test.exe";
            _configData[gameName] = new GameConfig { Name = gameName };

            ConfigCommand.Run(_serviceProvider, new[] { "remove", gameName });

            Assert.DoesNotContain(gameName, _configData.Keys);
            Assert.Equal($"Removed {gameName}.", _consoleOutput.ToString().Trim());
            _mockConfigProvider.Verify(p => p.Save(It.IsAny<IReadOnlyDictionary<string, GameConfig>>()), Times.Once);
        }

        [Fact]
        public void List_WithGames_PrintsGames()
        {
            _configData["a.exe"] = new GameConfig { Name = "a.exe", IsEnabled = true, HDREnabled = true };
            _configData["b.exe"] = new GameConfig { Name = "b.exe", IsEnabled = false, HDREnabled = false };

            ConfigCommand.Run(_serviceProvider, new[] { "list" });

            var output = _consoleOutput.ToString();
            Assert.Contains("a.exe  Enabled=True  HDR=True", output);
            Assert.Contains("b.exe  Enabled=False  HDR=False", output);
        }
    }
}