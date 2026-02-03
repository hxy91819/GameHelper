using System;
using System.Diagnostics;
using System.IO;
using GameHelper.ConsoleHost.Utilities;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace GameHelper.Tests
{
    public class GameMetadataExtractorTests
    {
        [Fact]
        public void ExtractMetadata_WithValidExe_ReturnsProductName()
        {
            // Arrange
            var exePath = typeof(GameMetadataExtractorTests).Assembly.Location;

            // Act
            var (productName, companyName) = GameMetadataExtractor.ExtractMetadata(exePath);

            // Assert
            // The test assembly should have some metadata
            Assert.NotNull(productName);
        }

        [Fact]
        public void ExtractMetadata_WithNullPath_ReturnsNull()
        {
            // Act
            var (productName, companyName) = GameMetadataExtractor.ExtractMetadata(null!);

            // Assert
            Assert.Null(productName);
            Assert.Null(companyName);
        }

        [Fact]
        public void ExtractMetadata_WithEmptyPath_ReturnsNull()
        {
            // Act
            var (productName, companyName) = GameMetadataExtractor.ExtractMetadata(string.Empty);

            // Assert
            Assert.Null(productName);
            Assert.Null(companyName);
        }

        [Fact]
        public void ExtractMetadata_WithNonExistentFile_ReturnsNull()
        {
            // Arrange
            var nonExistentPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString() + ".exe");

            // Act
            var (productName, companyName) = GameMetadataExtractor.ExtractMetadata(nonExistentPath);

            // Assert
            Assert.Null(productName);
            Assert.Null(companyName);
        }

        [Fact]
        public void ExtractMetadata_WithLogger_LogsDebugMessages()
        {
            // Arrange
            var exePath = typeof(GameMetadataExtractorTests).Assembly.Location;
            var mockLogger = new Mock<ILogger>();

            // Act
            var (productName, companyName) = GameMetadataExtractor.ExtractMetadata(exePath, mockLogger.Object);

            // Assert
            mockLogger.Verify(
                x => x.Log(
                    LogLevel.Debug,
                    It.IsAny<EventId>(),
                    It.Is<It.IsAnyType>((v, t) => v.ToString()!.Contains("Extracted metadata")),
                    null,
                    It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
                Times.Once);
        }

        [Fact]
        public void GenerateBaseDataKey_WithProductName_ReturnsNormalizedProductName()
        {
            // Arrange
            var exePath = @"C:\Games\MyGame\game.exe";
            var productName = "My Awesome Game";

            // Act
            var result = DataKeyGenerator.GenerateBaseDataKey(exePath, productName);

            // Assert
            Assert.Equal("myawesomegame", result); // Normalized: lowercase, no spaces
        }

        [Fact]
        public void GenerateBaseDataKey_WithoutProductName_ReturnsFileName()
        {
            // Arrange
            var exePath = @"C:\Games\MyGame\game.exe";

            // Act
            var result = DataKeyGenerator.GenerateBaseDataKey(exePath, null);

            // Assert
            Assert.Equal("game", result);
        }

        [Fact]
        public void GenerateBaseDataKey_WithEmptyProductName_ReturnsFileName()
        {
            // Arrange
            var exePath = @"C:\Games\MyGame\game.exe";

            // Act
            var result = DataKeyGenerator.GenerateBaseDataKey(exePath, "   ");

            // Assert
            Assert.Equal("game", result);
        }

        [Fact]
        public void GenerateBaseDataKey_WithComplexPath_ReturnsCorrectFileName()
        {
            // Arrange
            var exePath = @"C:\Program Files (x86)\Steam\steamapps\common\MyGame\bin\x64\game.exe";

            // Act
            var result = DataKeyGenerator.GenerateBaseDataKey(exePath, null);

            // Assert
            Assert.Equal("game", result);
        }
    }
}
