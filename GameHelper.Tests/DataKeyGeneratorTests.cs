using System;
using System.Collections.Generic;
using GameHelper.ConsoleHost.Utilities;
using GameHelper.Core.Abstractions;
using GameHelper.Core.Models;
using Moq;
using Xunit;

namespace GameHelper.Tests
{
    public class DataKeyGeneratorTests
    {
        [Fact]
        public void GenerateBaseDataKey_WithProductName_ReturnsNormalizedProductName()
        {
            // Arrange
            var exePath = @"C:\Games\SomeGame\game.exe";
            var productName = "Awesome Game";

            // Act
            var result = DataKeyGenerator.GenerateBaseDataKey(exePath, productName);

            // Assert
            Assert.Equal("awesomegame", result); // Normalized: lowercase, no spaces
        }

        [Fact]
        public void GenerateBaseDataKey_WithoutProductName_ReturnsNormalizedFileName()
        {
            // Arrange
            var exePath = @"C:\Games\SomeGame\game.exe";
            string? productName = null;

            // Act
            var result = DataKeyGenerator.GenerateBaseDataKey(exePath, productName);

            // Assert
            Assert.Equal("game", result);
        }

        [Fact]
        public void GenerateBaseDataKey_WithSpaces_RemovesSpaces()
        {
            // Arrange
            var exePath = @"C:\Games\Tales of Arise.exe";

            // Act
            var result = DataKeyGenerator.GenerateBaseDataKey(exePath);

            // Assert
            Assert.Equal("talesofarise", result); // Spaces removed
        }

        [Fact]
        public void GenerateBaseDataKey_WithUnderscores_PreservesUnderscores()
        {
            // Arrange
            var exePath = @"C:\Games\Project_Plague.exe";

            // Act
            var result = DataKeyGenerator.GenerateBaseDataKey(exePath);

            // Assert
            Assert.Equal("project_plague", result); // Underscores preserved
        }

        [Fact]
        public void GenerateBaseDataKey_WithMixedCase_ConvertsToLowercase()
        {
            // Arrange
            var exePath = @"C:\Games\DWORIGINS.exe";

            // Act
            var result = DataKeyGenerator.GenerateBaseDataKey(exePath);

            // Assert
            Assert.Equal("dworigins", result);
        }

        [Fact]
        public void GenerateBaseDataKey_WithSpecialCharacters_RemovesSpecialCharacters()
        {
            // Arrange
            var exePath = @"C:\Games\Game!@#$%^&*().exe";

            // Act
            var result = DataKeyGenerator.GenerateBaseDataKey(exePath);

            // Assert
            Assert.Equal("game", result); // Special characters removed
        }

        [Fact]
        public void GenerateUniqueDataKey_WhenKeyDoesNotExist_ReturnsBaseKey()
        {
            // Arrange
            var exePath = @"C:\Games\NewGame.exe";
            var mockProvider = new Mock<IConfigProvider>();
            mockProvider.Setup(p => p.Load()).Returns(new Dictionary<string, GameConfig>());

            // Act
            var result = DataKeyGenerator.GenerateUniqueDataKey(exePath, null, mockProvider.Object);

            // Assert
            Assert.Equal("newgame", result);
        }

        [Fact]
        public void GenerateUniqueDataKey_WhenKeyExists_AppendsNumericSuffix()
        {
            // Arrange
            var exePath = @"C:\Games\RE.exe";
            var mockProvider = new Mock<IConfigProvider>();
            mockProvider.Setup(p => p.Load()).Returns(new Dictionary<string, GameConfig>
            {
                ["RE.exe"] = new GameConfig { DataKey = "re", ExecutableName = "RE.exe" }
            });

            // Act
            var result = DataKeyGenerator.GenerateUniqueDataKey(exePath, null, mockProvider.Object);

            // Assert
            Assert.Equal("re2", result); // Appended "2" for uniqueness
        }

        [Fact]
        public void GenerateUniqueDataKey_WhenMultipleKeysExist_FindsNextAvailableSuffix()
        {
            // Arrange
            var exePath = @"C:\Games\RE.exe";
            var mockProvider = new Mock<IConfigProvider>();
            mockProvider.Setup(p => p.Load()).Returns(new Dictionary<string, GameConfig>
            {
                ["RE.exe"] = new GameConfig { DataKey = "re", ExecutableName = "RE.exe" },
                ["RE2.exe"] = new GameConfig { DataKey = "re2", ExecutableName = "RE2.exe" },
                ["RE3.exe"] = new GameConfig { DataKey = "re3", ExecutableName = "RE3.exe" }
            });

            // Act
            var result = DataKeyGenerator.GenerateUniqueDataKey(exePath, null, mockProvider.Object);

            // Assert
            Assert.Equal("re4", result); // Found next available suffix
        }

        [Fact]
        public void GenerateBaseDataKey_WithGenericProductName_UsesFileName()
        {
            // Arrange
            var exePath = @"C:\Games\MyGame.exe";
            var productName = "Game"; // Too generic

            // Act
            var result = DataKeyGenerator.GenerateBaseDataKey(exePath, productName);

            // Assert
            Assert.Equal("mygame", result); // Falls back to filename
        }

        [Fact]
        public void GenerateBaseDataKey_WithShortProductName_UsesFileName()
        {
            // Arrange
            var exePath = @"C:\Games\MyGame.exe";
            var productName = "AB"; // Too short

            // Act
            var result = DataKeyGenerator.GenerateBaseDataKey(exePath, productName);

            // Assert
            Assert.Equal("mygame", result); // Falls back to filename
        }
    }
}
