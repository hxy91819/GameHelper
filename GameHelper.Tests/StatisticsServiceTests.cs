using System;
using System.Collections.Generic;
using GameHelper.Core.Abstractions;
using GameHelper.Core.Models;
using GameHelper.Core.Services;
using Moq;
using Xunit;

namespace GameHelper.Tests
{
    public class StatisticsServiceTests
    {
        [Fact]
        public void GetOverview_CalculatesMinutesCorrectly()
        {
            // Arrange
            var mockPlaytime = new Mock<IPlaytimeSnapshotProvider>();
            var mockConfig = new Mock<IConfigProvider>();

            var now = DateTime.Now;
            var recentSession = new PlaySession("game1", now.AddMinutes(-10), now, TimeSpan.FromMinutes(10), 10);
            var oldSession = new PlaySession("game1", now.AddDays(-20), now.AddDays(-20).AddMinutes(20), TimeSpan.FromMinutes(20), 20);

            var record = new GamePlaytimeRecord
            {
                GameName = "game1",
                Sessions = new List<PlaySession> { recentSession, oldSession }
            };

            mockPlaytime.Setup(p => p.GetPlaytimeRecords())
                .Returns(new List<GamePlaytimeRecord> { record });

            mockConfig.Setup(c => c.Load())
                .Returns(new Dictionary<string, GameConfig>());

            var service = new StatisticsService(mockPlaytime.Object, mockConfig.Object);

            // Act
            var overview = service.GetOverview();

            // Assert
            Assert.Single(overview);
            var summary = overview[0];
            Assert.Equal("game1", summary.GameName);
            Assert.Equal(30, summary.TotalMinutes); // 10 + 20
            Assert.Equal(10, summary.RecentMinutes); // Only the recent one
            Assert.Equal(2, summary.SessionCount);
        }
    }
}
