using System;
using System.Collections.Generic;
using GameHelper.Core.Abstractions;
using GameHelper.Core.Models;
using GameHelper.Core.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace GameHelper.Tests
{
    /// <summary>
    /// 测试 Story 1.7 的模糊匹配安全增强功能：
    /// - 动态阈值计算
    /// - 路径相关性验证
    /// - 系统路径黑名单
    /// </summary>
    public class FuzzyMatchingSafetyTests
    {
        // Fakes
        private sealed class FakeMonitor : IProcessMonitor
        {
            public event Action<ProcessEventInfo>? ProcessStarted;
            public event Action<ProcessEventInfo>? ProcessStopped;
            public void Start() { }
            public void Stop() { }
            public void Dispose() { }
            public void RaiseStart(ProcessEventInfo info) => ProcessStarted?.Invoke(info);
            public void RaiseStop(ProcessEventInfo info) => ProcessStopped?.Invoke(info);
        }

        private sealed class FakeHdr : IHdrController
        {
            public bool IsEnabled { get; private set; }
            public void Enable() => IsEnabled = true;
            public void Disable() => IsEnabled = false;
        }

        private sealed class FakePlayTime : IPlayTimeService
        {
            public List<string> Started { get; } = new();
            public List<string> Stopped { get; } = new();
            public void StartTracking(string gameName) => Started.Add(gameName);
            public PlaySession? StopTracking(string gameName)
            {
                Stopped.Add(gameName);
                return new PlaySession(gameName, DateTime.MinValue, DateTime.MinValue, TimeSpan.Zero, 0);
            }
        }

        private sealed class FakeConfig : IConfigProvider
        {
            private readonly IReadOnlyDictionary<string, GameConfig> _configs;
            public FakeConfig(IReadOnlyDictionary<string, GameConfig> configs) => _configs = configs;
            public IReadOnlyDictionary<string, GameConfig> Load() => _configs;
            public void Save(IReadOnlyDictionary<string, GameConfig> configs) { }
        }

        private static GameAutomationService CreateService(params GameConfig[] configs)
        {
            var dict = new Dictionary<string, GameConfig>(StringComparer.OrdinalIgnoreCase);
            foreach (var config in configs)
            {
                if (!string.IsNullOrWhiteSpace(config.DataKey))
                {
                    dict[config.DataKey] = config;
                }
            }

            var monitor = new FakeMonitor();
            var configProvider = new FakeConfig(dict);
            var hdr = new FakeHdr();
            var playTime = new FakePlayTime();
            var logger = NullLogger<GameAutomationService>.Instance;

            var service = new GameAutomationService(monitor, configProvider, hdr, playTime, logger);
            service.Start();
            return service;
        }

        #region AC5: 动态阈值测试

        [Fact]
        public void ShortName_ExactMatch_Accepts()
        {
            // Arrange: 2字符短名称，完全匹配
            var config = new GameConfig
            {
                DataKey = "RE.exe",
                ExecutableName = "RE.exe",
                ExecutablePath = @"D:\Games\RE\RE.exe",
                IsEnabled = true
            };

            var service = CreateService(config);
            var monitor = (FakeMonitor)typeof(GameAutomationService)
                .GetField("_monitor", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
                .GetValue(service)!;

            var playTime = (FakePlayTime)typeof(GameAutomationService)
                .GetField("_playTime", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
                .GetValue(service)!;

            // Act: 完全匹配
            monitor.RaiseStart(new ProcessEventInfo("RE.exe", @"D:\Games\RE\RE.exe"));

            // Assert: 应该成功匹配
            Assert.Contains(playTime.Started, s => string.Equals(s, "RE.exe", StringComparison.OrdinalIgnoreCase));
        }

        [Theory]
        [InlineData("RE.exe", @"D:\Games\RE\RE.exe")]
        [InlineData("ABC.exe", @"D:\Games\ABC\ABC.exe")]
        [InlineData("Test.exe", @"D:\Games\Test\Test.exe")]
        public void ShortName_RequiresHighThreshold(string configName, string configPath)
        {
            // Arrange: 配置短名称游戏，验证阈值为95
            var config = new GameConfig
            {
                DataKey = configName,
                ExecutableName = configName,
                ExecutablePath = configPath,
                IsEnabled = true
            };

            var service = CreateService(config);
            var monitor = (FakeMonitor)typeof(GameAutomationService)
                .GetField("_monitor", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
                .GetValue(service)!;

            var playTime = (FakePlayTime)typeof(GameAutomationService)
                .GetField("_playTime", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
                .GetValue(service)!;

            // Act: 完全匹配应该成功
            monitor.RaiseStart(new ProcessEventInfo(configName, configPath));

            // Assert
            Assert.Contains(playTime.Started, s => string.Equals(s, configName, StringComparison.OrdinalIgnoreCase));
        }

        [Theory]
        [InlineData("Game5.exe", @"D:\Games\Game5\Game5.exe")]
        [InlineData("MyGame.exe", @"D:\Games\MyGame\MyGame.exe")]
        [InlineData("LongGame.exe", @"D:\Games\LongGame\LongGame.exe")]
        public void MediumName_RequiresMediumThreshold(string configName, string configPath)
        {
            // Arrange: 配置中等名称游戏，验证阈值为90
            var config = new GameConfig
            {
                DataKey = configName,
                ExecutableName = configName,
                ExecutablePath = configPath,
                IsEnabled = true
            };

            var service = CreateService(config);
            var monitor = (FakeMonitor)typeof(GameAutomationService)
                .GetField("_monitor", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
                .GetValue(service)!;

            var playTime = (FakePlayTime)typeof(GameAutomationService)
                .GetField("_playTime", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
                .GetValue(service)!;

            // Act: 完全匹配应该成功
            monitor.RaiseStart(new ProcessEventInfo(configName, configPath));

            // Assert
            Assert.Contains(playTime.Started, s => string.Equals(s, configName, StringComparison.OrdinalIgnoreCase));
        }

        [Fact]
        public void LongName_RequiresStandardThreshold()
        {
            // Arrange: 配置长名称游戏，验证阈值为80
            var config = new GameConfig
            {
                DataKey = "VeryLongGameName.exe",
                ExecutableName = "VeryLongGameName.exe",
                ExecutablePath = @"D:\Games\VeryLongGameName\VeryLongGameName.exe",
                IsEnabled = true
            };

            var service = CreateService(config);
            var monitor = (FakeMonitor)typeof(GameAutomationService)
                .GetField("_monitor", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
                .GetValue(service)!;

            var playTime = (FakePlayTime)typeof(GameAutomationService)
                .GetField("_playTime", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
                .GetValue(service)!;

            // Act: 完全匹配应该成功
            monitor.RaiseStart(new ProcessEventInfo("VeryLongGameName.exe", @"D:\Games\VeryLongGameName\VeryLongGameName.exe"));

            // Assert
            Assert.Contains(playTime.Started, s => string.Equals(s, "VeryLongGameName.exe", StringComparison.OrdinalIgnoreCase));
        }

        #endregion

        #region AC5: 路径相关性测试

        [Fact]
        public void PathRelated_SameDirectory_Accepts()
        {
            // Arrange: 配置和进程在同一目录
            var config = new GameConfig
            {
                DataKey = "Game.exe",
                ExecutableName = "Game.exe",
                ExecutablePath = @"D:\Games\MyGame\Game.exe",
                IsEnabled = true
            };

            var service = CreateService(config);
            var monitor = (FakeMonitor)typeof(GameAutomationService)
                .GetField("_monitor", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
                .GetValue(service)!;

            var playTime = (FakePlayTime)typeof(GameAutomationService)
                .GetField("_playTime", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
                .GetValue(service)!;

            // Act: 完全相同的路径
            monitor.RaiseStart(new ProcessEventInfo("Game.exe", @"D:\Games\MyGame\Game.exe"));

            // Assert
            Assert.Contains(playTime.Started, s => string.Equals(s, "Game.exe", StringComparison.OrdinalIgnoreCase));
        }

        [Fact]
        public void PathRelated_Subdirectory_Accepts()
        {
            // Arrange
            var config = new GameConfig
            {
                DataKey = "Game.exe",
                ExecutableName = "Game.exe",
                ExecutablePath = @"D:\Games\MyGame\Game.exe",
                IsEnabled = true
            };

            var service = CreateService(config);
            var monitor = (FakeMonitor)typeof(GameAutomationService)
                .GetField("_monitor", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
                .GetValue(service)!;

            var playTime = (FakePlayTime)typeof(GameAutomationService)
                .GetField("_playTime", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
                .GetValue(service)!;

            // Act: 进程在子目录
            monitor.RaiseStart(new ProcessEventInfo("Game.exe", @"D:\Games\MyGame\bin\Game.exe"));

            // Assert
            Assert.Contains(playTime.Started, s => string.Equals(s, "Game.exe", StringComparison.OrdinalIgnoreCase));
        }

        [Fact]
        public void PathRelated_DifferentDirectory_Rejects()
        {
            // Arrange
            var config = new GameConfig
            {
                DataKey = "Game.exe",
                ExecutableName = "Game.exe",
                ExecutablePath = @"D:\Games\MyGame\Game.exe",
                IsEnabled = true
            };

            var service = CreateService(config);
            var monitor = (FakeMonitor)typeof(GameAutomationService)
                .GetField("_monitor", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
                .GetValue(service)!;

            var playTime = (FakePlayTime)typeof(GameAutomationService)
                .GetField("_playTime", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
                .GetValue(service)!;

            // Act: 完全不同的目录
            monitor.RaiseStart(new ProcessEventInfo("Game.exe", @"D:\OtherGames\Game.exe"));

            // Assert
            Assert.DoesNotContain(playTime.Started, s => string.Equals(s, "Game.exe", StringComparison.OrdinalIgnoreCase));
        }

        #endregion

        #region AC5: 系统路径黑名单测试

        [Theory]
        [InlineData(@"C:\Windows\System32\reg.exe")]
        [InlineData(@"C:\Windows\SysWOW64\rg.exe")]
        [InlineData(@"C:\Windows\notepad.exe")]
        [InlineData(@"c:\windows\system32\cmd.exe")]  // 测试大小写不敏感
        public void SystemPath_AlwaysRejected(string systemPath)
        {
            // Arrange: 配置一个短名称游戏（容易误匹配）
            var config = new GameConfig
            {
                DataKey = "RE.exe",
                ExecutableName = "RE.exe",
                ExecutablePath = @"D:\Games\RE\RE.exe",
                IsEnabled = true
            };

            var service = CreateService(config);
            var monitor = (FakeMonitor)typeof(GameAutomationService)
                .GetField("_monitor", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
                .GetValue(service)!;

            var playTime = (FakePlayTime)typeof(GameAutomationService)
                .GetField("_playTime", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
                .GetValue(service)!;

            // Act: 尝试启动系统路径的进程
            var processName = System.IO.Path.GetFileName(systemPath);
            monitor.RaiseStart(new ProcessEventInfo(processName, systemPath));

            // Assert: 不应该匹配
            Assert.Empty(playTime.Started);
        }

        [Theory]
        [InlineData(@"D:\Games\RE.exe")]
        [InlineData(@"C:\Program Files\Game\game.exe")]
        [InlineData(@"E:\SteamLibrary\game.exe")]
        public void NonSystemPath_NotBlocked(string gamePath)
        {
            // Arrange
            var processName = System.IO.Path.GetFileName(gamePath);
            var config = new GameConfig
            {
                DataKey = processName,
                ExecutableName = processName,
                ExecutablePath = gamePath,
                IsEnabled = true
            };

            var service = CreateService(config);
            var monitor = (FakeMonitor)typeof(GameAutomationService)
                .GetField("_monitor", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
                .GetValue(service)!;

            var playTime = (FakePlayTime)typeof(GameAutomationService)
                .GetField("_playTime", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
                .GetValue(service)!;

            // Act
            monitor.RaiseStart(new ProcessEventInfo(processName, gamePath));

            // Assert: 应该正常匹配
            Assert.Contains(playTime.Started, s => string.Equals(s, processName, StringComparison.OrdinalIgnoreCase));
        }

        #endregion

        #region AC6: 回归测试

        [Fact]
        public void RegressionTest_RegExe_DoesNotMatch_REExe()
        {
            // Arrange: 配置 RE.exe 游戏
            var config = new GameConfig
            {
                DataKey = "RE.exe",
                ExecutableName = "RE.exe",
                ExecutablePath = @"D:\Games\Romantic.Escapades.v2.0.2\game\RE.exe",
                IsEnabled = true
            };

            var service = CreateService(config);
            var monitor = (FakeMonitor)typeof(GameAutomationService)
                .GetField("_monitor", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
                .GetValue(service)!;

            var playTime = (FakePlayTime)typeof(GameAutomationService)
                .GetField("_playTime", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
                .GetValue(service)!;

            // Act: 尝试匹配 reg.exe（系统工具）
            monitor.RaiseStart(new ProcessEventInfo("reg.exe", @"C:\Windows\System32\reg.exe"));

            // Assert: 应该拒绝匹配
            Assert.Empty(playTime.Started);
        }

        [Fact]
        public void RegressionTest_RgExe_DoesNotMatch_REExe()
        {
            // Arrange: 配置 RE.exe 游戏
            var config = new GameConfig
            {
                DataKey = "RE.exe",
                ExecutableName = "RE.exe",
                ExecutablePath = @"D:\Games\Romantic.Escapades.v2.0.2\game\RE.exe",
                IsEnabled = true
            };

            var service = CreateService(config);
            var monitor = (FakeMonitor)typeof(GameAutomationService)
                .GetField("_monitor", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
                .GetValue(service)!;

            var playTime = (FakePlayTime)typeof(GameAutomationService)
                .GetField("_playTime", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
                .GetValue(service)!;

            // Act: 尝试匹配 rg.exe（ripgrep 工具）
            monitor.RaiseStart(new ProcessEventInfo("rg.exe", @"C:\Program Files\ripgrep\rg.exe"));

            // Assert: 应该拒绝匹配（即使不在系统路径，但分数不够）
            Assert.Empty(playTime.Started);
        }

        [Fact]
        public void RegressionTest_LegitimateREExe_StillMatches()
        {
            // Arrange: 配置 RE.exe 游戏
            var config = new GameConfig
            {
                DataKey = "RE.exe",
                ExecutableName = "RE.exe",
                ExecutablePath = @"D:\Games\Romantic.Escapades.v2.0.2\game\RE.exe",
                IsEnabled = true
            };

            var service = CreateService(config);
            var monitor = (FakeMonitor)typeof(GameAutomationService)
                .GetField("_monitor", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
                .GetValue(service)!;

            var playTime = (FakePlayTime)typeof(GameAutomationService)
                .GetField("_playTime", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
                .GetValue(service)!;

            // Act: 匹配合法的 RE.exe
            monitor.RaiseStart(new ProcessEventInfo("RE.exe", @"D:\Games\Romantic.Escapades.v2.0.2\game\RE.exe"));

            // Assert: 应该成功匹配
            Assert.Contains(playTime.Started, s => string.Equals(s, "RE.exe", StringComparison.OrdinalIgnoreCase));
        }

        #endregion

        #region AC7: 旧配置兼容性测试

        [Fact]
        public void LegacyConfig_WithoutExecutablePath_SkipsPathValidation()
        {
            // Arrange: 旧配置（仅有 ExecutableName，无 ExecutablePath）
            var config = new GameConfig
            {
                DataKey = "OldGame.exe",
                ExecutableName = "OldGame.exe",
                ExecutablePath = null,  // 旧配置没有路径
                IsEnabled = true
            };

            var service = CreateService(config);
            var monitor = (FakeMonitor)typeof(GameAutomationService)
                .GetField("_monitor", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
                .GetValue(service)!;

            var playTime = (FakePlayTime)typeof(GameAutomationService)
                .GetField("_playTime", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
                .GetValue(service)!;

            // Act: 从任意路径启动（非系统路径）
            monitor.RaiseStart(new ProcessEventInfo("OldGame.exe", @"D:\AnyPath\OldGame.exe"));

            // Assert: 应该成功匹配（跳过路径验证）
            Assert.Contains(playTime.Started, s => string.Equals(s, "OldGame.exe", StringComparison.OrdinalIgnoreCase));
        }

        [Fact]
        public void LegacyConfig_StillBlockedBySystemPath()
        {
            // Arrange: 旧配置
            var config = new GameConfig
            {
                DataKey = "cmd.exe",
                ExecutableName = "cmd.exe",
                ExecutablePath = null,
                IsEnabled = true
            };

            var service = CreateService(config);
            var monitor = (FakeMonitor)typeof(GameAutomationService)
                .GetField("_monitor", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
                .GetValue(service)!;

            var playTime = (FakePlayTime)typeof(GameAutomationService)
                .GetField("_playTime", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
                .GetValue(service)!;

            // Act: 系统路径
            monitor.RaiseStart(new ProcessEventInfo("cmd.exe", @"C:\Windows\System32\cmd.exe"));

            // Assert: 应该被黑名单拒绝
            Assert.Empty(playTime.Started);
        }

        [Fact]
        public void LegacyConfig_StillSubjectToDynamicThreshold()
        {
            // Arrange: 旧配置，短名称
            var config = new GameConfig
            {
                DataKey = "AB.exe",
                ExecutableName = "AB.exe",
                ExecutablePath = null,
                IsEnabled = true
            };

            var service = CreateService(config);
            var monitor = (FakeMonitor)typeof(GameAutomationService)
                .GetField("_monitor", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
                .GetValue(service)!;

            var playTime = (FakePlayTime)typeof(GameAutomationService)
                .GetField("_playTime", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
                .GetValue(service)!;

            // Act: 尝试匹配相似但不够高分的名称
            monitor.RaiseStart(new ProcessEventInfo("AC.exe", @"D:\Games\AC.exe"));

            // Assert: 应该被动态阈值拒绝（2字符需要95分）
            Assert.Empty(playTime.Started);
        }

        #endregion
    }
}
