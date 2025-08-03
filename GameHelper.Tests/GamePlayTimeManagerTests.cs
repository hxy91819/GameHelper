using System;
using System.IO;
using Xunit;

namespace GameHelper.Tests
{
    public class GamePlayTimeManagerTests
    {
        private readonly string _testConfigDirectory;
        private readonly GamePlayTimeManager _playTimeManager;
        
        public GamePlayTimeManagerTests()
        {
            // 创建临时配置目录用于测试
            _testConfigDirectory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
            Directory.CreateDirectory(_testConfigDirectory);
            _playTimeManager = new GamePlayTimeManager(_testConfigDirectory);
        }
        
        [Fact]
        public void StartAndStopGamePlayTimeTracking_Should_Record_Session()
        {
            // Arrange
            var gameName = "TestGame";
            
            // Act
            _playTimeManager.StartGamePlayTimeTracking(gameName);
            // 模拟游戏运行1分钟
            System.Threading.Thread.Sleep(100); // 短暂等待以确保时间差
            _playTimeManager.StopGamePlayTimeTracking(gameName);
            
            // Assert
            var gamePlayTime = _playTimeManager.GetGamePlayTime(gameName);
            Assert.NotNull(gamePlayTime);
            Assert.Equal(gameName, gamePlayTime.GameName);
            Assert.Single(gamePlayTime.Sessions);
            Assert.True(gamePlayTime.Sessions[0].DurationMinutes >= 0);
        }
        
        [Fact]
        public void GetWeeklyPlayTime_Should_Return_Correct_Minutes()
        {
            // Arrange
            var gameName = "TestGame";
            var startTime = DateTime.Now.AddDays(-3); // 3天前开始游戏
            var endTime = startTime.AddMinutes(120); // 游戏时长2小时
            
            // 直接操作GamePlayTimeManager的内部状态
            var gamePlayTime = new GamePlayTime { GameName = gameName };
            gamePlayTime.Sessions.Add(new GameSession
            {
                StartTime = startTime,
                EndTime = endTime,
                DurationMinutes = 120
            });
            
            // 使用反射来访问私有字段并添加游戏记录
            var gamePlayTimesField = typeof(GamePlayTimeManager).GetField("gamePlayTimes", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            var gamePlayTimes = (Dictionary<string, GamePlayTime>)gamePlayTimesField.GetValue(_playTimeManager);
            gamePlayTimes[gameName] = gamePlayTime;
            
            // Act
            var weeklyPlayTime = _playTimeManager.GetWeeklyPlayTime(gameName);
            
            // Assert
            Assert.Equal(120, weeklyPlayTime);
        }
        
        [Fact]
        public void GetMonthlyPlayTime_Should_Return_Correct_Minutes()
        {
            // Arrange
            var gameName = "TestGame";
            var startTime = DateTime.Now.AddMonths(-1).AddDays(5); // 1个月前开始游戏
            var endTime = startTime.AddMinutes(90); // 游戏时长1.5小时
            
            // 直接操作GamePlayTimeManager的内部状态
            var gamePlayTime = new GamePlayTime { GameName = gameName };
            gamePlayTime.Sessions.Add(new GameSession
            {
                StartTime = startTime,
                EndTime = endTime,
                DurationMinutes = 90
            });
            
            // 使用反射来访问私有字段并添加游戏记录
            var gamePlayTimesField = typeof(GamePlayTimeManager).GetField("gamePlayTimes", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            var gamePlayTimes = (Dictionary<string, GamePlayTime>)gamePlayTimesField.GetValue(_playTimeManager);
            gamePlayTimes[gameName] = gamePlayTime;
            
            // Act
            var monthlyPlayTime = _playTimeManager.GetMonthlyPlayTime(gameName);
            
            // Assert
            Assert.Equal(90, monthlyPlayTime);
        }
        
        [Fact]
        public void GetYearlyPlayTime_Should_Return_Correct_Minutes()
        {
            // Arrange
            var gameName = "TestGame";
            var startTime = DateTime.Now.AddMonths(-6); // 6个月前开始游戏
            var endTime = startTime.AddMinutes(60); // 游戏时长1小时
            
            // 直接操作GamePlayTimeManager的内部状态
            var gamePlayTime = new GamePlayTime { GameName = gameName };
            gamePlayTime.Sessions.Add(new GameSession
            {
                StartTime = startTime,
                EndTime = endTime,
                DurationMinutes = 60
            });
            
            // 使用反射来访问私有字段并添加游戏记录
            var gamePlayTimesField = typeof(GamePlayTimeManager).GetField("gamePlayTimes", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            var gamePlayTimes = (Dictionary<string, GamePlayTime>)gamePlayTimesField.GetValue(_playTimeManager);
            gamePlayTimes[gameName] = gamePlayTime;
            
            // Act
            var yearlyPlayTime = _playTimeManager.GetYearlyPlayTime(gameName);
            
            // Assert
            Assert.Equal(60, yearlyPlayTime);
        }
        
        [Fact]
        public void FormatPlayTime_Should_Return_Correct_Format()
        {
            // Act & Assert
            Assert.Equal("0分钟", _playTimeManager.FormatPlayTime(0));
            Assert.Equal("30分钟", _playTimeManager.FormatPlayTime(30));
            Assert.Equal("1小时30分钟", _playTimeManager.FormatPlayTime(90));
            Assert.Equal("2小时0分钟", _playTimeManager.FormatPlayTime(120));
        }
        
        [Fact]
        public void GetSessionCountInPeriod_Should_Return_Correct_Count()
        {
            // Arrange
            var gameName = "TestGame";
            var startTime1 = DateTime.Now.AddDays(-5);
            var endTime1 = startTime1.AddMinutes(60);
            
            var startTime2 = DateTime.Now.AddDays(-2);
            var endTime2 = startTime2.AddMinutes(45);
            
            // 直接操作GamePlayTimeManager的内部状态
            var gamePlayTime = new GamePlayTime { GameName = gameName };
            gamePlayTime.Sessions.Add(new GameSession
            {
                StartTime = startTime1,
                EndTime = endTime1,
                DurationMinutes = 60
            });
            
            gamePlayTime.Sessions.Add(new GameSession
            {
                StartTime = startTime2,
                EndTime = endTime2,
                DurationMinutes = 45
            });
            
            // 使用反射来访问私有字段并添加游戏记录
            var gamePlayTimesField = typeof(GamePlayTimeManager).GetField("gamePlayTimes", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            var gamePlayTimes = (Dictionary<string, GamePlayTime>)gamePlayTimesField.GetValue(_playTimeManager);
            gamePlayTimes[gameName] = gamePlayTime;
            
            // Act
            var sessionCount = _playTimeManager.GetSessionCountInPeriod(gameName, DateTime.Now.AddDays(-6), DateTime.Now);
            
            // Assert
            Assert.Equal(2, sessionCount);
        }
        
        public void Dispose()
        {
            // 清理测试目录
            if (Directory.Exists(_testConfigDirectory))
            {
                Directory.Delete(_testConfigDirectory, true);
            }
        }
    }
}
