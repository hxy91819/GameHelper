using System;
using System.Collections.Generic;
using GameHelper.Core.Abstractions;
using GameHelper.Core.Models;
using GameHelper.Core.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using CoreGameConfig = GameHelper.Core.Models.GameConfig;

namespace GameHelper.Tests
{
    /// <summary>
    /// 重构安全网测试：补充 GameAutomationService 现有测试未覆盖的边界场景。
    /// 这些测试在重构期间充当行为快照——任何拆分步骤若导致测试失败，
    /// 说明搬家时改动了逻辑。
    /// </summary>
    public class GameAutomationServiceRefactorSafetyTests
    {
        // ========== Fakes ==========

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
            public int EnableCalls { get; private set; }
            public int DisableCalls { get; private set; }
            public bool IsEnabled { get; private set; }
            public void Enable() { IsEnabled = true; EnableCalls++; }
            public void Disable() { IsEnabled = false; DisableCalls++; }
        }

        private sealed class FakePlayTime : IPlayTimeService
        {
            public int StartCalls { get; private set; }
            public int StopCalls { get; private set; }
            public List<string> Started { get; } = new();
            public List<string> Stopped { get; } = new();
            public void StartTracking(string gameName)
            {
                StartCalls++;
                Started.Add(gameName);
            }
            public PlaySession? StopTracking(string gameName)
            {
                StopCalls++;
                Stopped.Add(gameName);
                return new PlaySession(gameName, DateTime.MinValue, DateTime.MinValue, TimeSpan.Zero, 0);
            }
        }

        private sealed class FakeConfig : IConfigProvider
        {
            private readonly IReadOnlyDictionary<string, CoreGameConfig> _configs;
            public FakeConfig(IReadOnlyDictionary<string, CoreGameConfig> configs) => _configs = configs;
            public IReadOnlyDictionary<string, CoreGameConfig> Load() => _configs;
            public void Save(IReadOnlyDictionary<string, CoreGameConfig> configs) { }
        }

        private static GameAutomationService CreateService(
            IProcessMonitor monitor,
            IConfigProvider cfg,
            IHdrController hdr,
            IPlayTimeService play,
            ILogger<GameAutomationService>? logger = null)
        {
            var svc = new GameAutomationService(monitor, cfg, hdr, play, logger ?? NullLogger<GameAutomationService>.Instance);
            svc.Start();
            return svc;
        }

        // ========== 测试场景 ==========

        #region 1. 多进程引用计数

        [Fact]
        public void SameDataKey_MultipleProcesses_RefCounting_TracksOnce()
        {
            // Arrange: 同一游戏，多个进程实例
            var monitor = new FakeMonitor();
            var cfg = new FakeConfig(new Dictionary<string, CoreGameConfig>(StringComparer.OrdinalIgnoreCase)
            {
                ["multi"] = new()
                {
                    DataKey = "multi",
                    ExecutableName = "game.exe",
                    ExecutablePath = @"C:\Games\game.exe",
                    IsEnabled = true,
                    HDREnabled = true
                }
            });
            var hdr = new FakeHdr();
            var play = new FakePlayTime();
            var svc = CreateService(monitor, cfg, hdr, play);

            // Act: 启动 3 个同名进程
            monitor.RaiseStart(new ProcessEventInfo("game.exe", @"C:\Games\game.exe"));
            monitor.RaiseStart(new ProcessEventInfo("game.exe", @"C:\Games\game.exe"));
            monitor.RaiseStart(new ProcessEventInfo("game.exe", @"C:\Games\game.exe"));

            // Assert: 只计一次
            Assert.Equal(1, play.StartCalls);
            Assert.Equal(1, hdr.EnableCalls);
            Assert.True(hdr.IsEnabled);

            // Act: 停止 2 个
            monitor.RaiseStop(new ProcessEventInfo("game.exe", @"C:\Games\game.exe"));
            monitor.RaiseStop(new ProcessEventInfo("game.exe", @"C:\Games\game.exe"));

            // Assert: 还不停止计时
            Assert.Equal(0, play.StopCalls);
            Assert.Equal(0, hdr.DisableCalls);
            Assert.True(hdr.IsEnabled);

            // Act: 停止最后一个
            monitor.RaiseStop(new ProcessEventInfo("game.exe", @"C:\Games\game.exe"));

            // Assert: 现在才停止
            Assert.Equal(1, play.StopCalls);
            Assert.Equal(1, hdr.DisableCalls);
            Assert.False(hdr.IsEnabled);
        }

        #endregion

        #region 2. L1 精确路径匹配

        [Fact]
        public void L1_ExactPathMatch_BypassesFuzzyMatching()
        {
            // Arrange: 配置有精确路径
            var monitor = new FakeMonitor();
            var cfg = new FakeConfig(new Dictionary<string, CoreGameConfig>(StringComparer.OrdinalIgnoreCase)
            {
                ["exact"] = new()
                {
                    DataKey = "exact",
                    ExecutableName = "something-else.exe",  // 名称故意不同
                    ExecutablePath = @"C:\Steam\game.exe",
                    IsEnabled = true,
                    HDREnabled = false
                }
            });
            var hdr = new FakeHdr();
            var play = new FakePlayTime();
            var svc = CreateService(monitor, cfg, hdr, play);

            // Act: 进程路径精确匹配，但名称不匹配
            monitor.RaiseStart(new ProcessEventInfo("launcher.exe", @"C:\Steam\game.exe"));

            // Assert: 仍然通过 L1 路径匹配成功
            Assert.Equal(1, play.StartCalls);
            Assert.Contains(play.Started, s => string.Equals(s, "exact", StringComparison.OrdinalIgnoreCase));
        }

        [Theory]
        [InlineData(@"C:\Steam\game.exe", @"C:\Steam\game.exe")]     // 标准形式
        [InlineData(@"C:\Steam\game.exe\", @"C:\Steam\game.exe")]    // 尾部多斜杠（进程侧）
        [InlineData(@"c:\steam\game.exe", @"C:\Steam\game.exe")]      // 大小写不同
        public void L1_PathNormalizationVariants_AllMatch(string processPath, string configPath)
        {
            var monitor = new FakeMonitor();
            var cfg = new FakeConfig(new Dictionary<string, CoreGameConfig>(StringComparer.OrdinalIgnoreCase)
            {
                ["norm"] = new()
                {
                    DataKey = "norm",
                    ExecutableName = "game.exe",
                    ExecutablePath = configPath,
                    IsEnabled = true,
                    HDREnabled = false
                }
            });
            var play = new FakePlayTime();
            var svc = CreateService(monitor, cfg, new FakeHdr(), play);

            monitor.RaiseStart(new ProcessEventInfo("game.exe", processPath));

            Assert.Equal(1, play.StartCalls);
        }

        [Fact]
        public void L1_DuplicateExecutablePath_KeepsFirstConfig()
        {
            var monitor = new FakeMonitor();
            var cfg = new FakeConfig(new Dictionary<string, CoreGameConfig>(StringComparer.OrdinalIgnoreCase)
            {
                ["first"] = new()
                {
                    DataKey = "first",
                    ExecutableName = "game.exe",
                    ExecutablePath = @"C:\Steam\game.exe",
                    IsEnabled = true
                },
                ["second"] = new()
                {
                    DataKey = "second",
                    ExecutableName = "game.exe",
                    ExecutablePath = @"C:\Steam\game.exe",
                    IsEnabled = true
                }
            });
            var play = new FakePlayTime();
            var svc = CreateService(monitor, cfg, new FakeHdr(), play);

            monitor.RaiseStart(new ProcessEventInfo("game.exe", @"C:\Steam\game.exe"));

            Assert.Equal(1, play.StartCalls);
            Assert.Contains("first", play.Started);
            Assert.DoesNotContain("second", play.Started);
        }

        #endregion

        #region 3. 歧义消解

        [Fact]
        public void AmbiguousName_SameExecutableNameDifferentPaths_PathRelatednessResolves()
        {
            // Arrange: 两个配置，同名但不同路径
            var monitor = new FakeMonitor();
            var cfg = new FakeConfig(new Dictionary<string, CoreGameConfig>(StringComparer.OrdinalIgnoreCase)
            {
                ["gameA"] = new()
                {
                    DataKey = "gameA",
                    ExecutableName = "game.exe",
                    ExecutablePath = @"C:\Games\A\game.exe",
                    IsEnabled = true
                },
                ["gameB"] = new()
                {
                    DataKey = "gameB",
                    ExecutableName = "game.exe",
                    ExecutablePath = @"C:\Games\B\game.exe",
                    IsEnabled = true
                }
            });
            var play = new FakePlayTime();
            var svc = CreateService(monitor, cfg, new FakeHdr(), play);

            // Act: 进程路径与 A 相关
            monitor.RaiseStart(new ProcessEventInfo("game.exe", @"C:\Games\A\bin\game.exe"));

            // Assert: 应该匹配到 A（路径相关），而不是 B
            Assert.Equal(1, play.StartCalls);
            Assert.Contains("gameA", play.Started);
            Assert.DoesNotContain("gameB", play.Started);
        }

        [Fact]
        public void AmbiguousName_NoPathRelatedness_AllRejected()
        {
            // Arrange: 两个同名配置，进程路径与两者都不相关
            var monitor = new FakeMonitor();
            var cfg = new FakeConfig(new Dictionary<string, CoreGameConfig>(StringComparer.OrdinalIgnoreCase)
            {
                ["gameA"] = new()
                {
                    DataKey = "gameA",
                    ExecutableName = "game.exe",
                    ExecutablePath = @"C:\Games\A\game.exe",
                    IsEnabled = true
                },
                ["gameB"] = new()
                {
                    DataKey = "gameB",
                    ExecutableName = "game.exe",
                    ExecutablePath = @"C:\Games\B\game.exe",
                    IsEnabled = true
                }
            });
            var play = new FakePlayTime();
            var svc = CreateService(monitor, cfg, new FakeHdr(), play);

            // Act: 进程在完全不相关的目录
            monitor.RaiseStart(new ProcessEventInfo("game.exe", @"D:\Random\game.exe"));

            // Assert: 歧义，应该拒绝
            Assert.Equal(0, play.StartCalls);
        }

        #endregion

        #region 4. 路径归一化边界

        [Fact]
        public void UncPath_WithTrailingSlash_NormalizedAndMatches()
        {
            var monitor = new FakeMonitor();
            var cfg = new FakeConfig(new Dictionary<string, CoreGameConfig>(StringComparer.OrdinalIgnoreCase)
            {
                ["unc"] = new()
                {
                    DataKey = "unc",
                    ExecutableName = "game.exe",
                    ExecutablePath = @"\\server\share\games\game.exe",
                    IsEnabled = true
                }
            });
            var play = new FakePlayTime();
            var svc = CreateService(monitor, cfg, new FakeHdr(), play);

            // 进程路径带尾部斜杠
            monitor.RaiseStart(new ProcessEventInfo("game.exe", @"\\server\share\games\game.exe\"));

            Assert.Equal(1, play.StartCalls);
        }

        [Fact]
        public void RelativePath_ResolvedToAbsolute()
        {
            var monitor = new FakeMonitor();
            var fullPath = System.IO.Path.GetFullPath("game.exe");
            var cfg = new FakeConfig(new Dictionary<string, CoreGameConfig>(StringComparer.OrdinalIgnoreCase)
            {
                ["rel"] = new()
                {
                    DataKey = "rel",
                    ExecutableName = "game.exe",
                    ExecutablePath = fullPath,
                    IsEnabled = true
                }
            });
            var play = new FakePlayTime();
            var svc = CreateService(monitor, cfg, new FakeHdr(), play);

            // 传入相对路径
            monitor.RaiseStart(new ProcessEventInfo("game.exe", "game.exe"));

            Assert.Equal(1, play.StartCalls);
        }

        #endregion

        #region 5. 交叉清理一致性

        [Fact]
        public void MultipleProcesses_DifferentPaths_StopOne_KeepsOtherActive()
        {
            // Arrange: 同一游戏，启动两个不同路径的进程实例
            var monitor = new FakeMonitor();
            var cfg = new FakeConfig(new Dictionary<string, CoreGameConfig>(StringComparer.OrdinalIgnoreCase)
            {
                ["cross"] = new()
                {
                    DataKey = "cross",
                    ExecutableName = "game.exe",
                    ExecutablePath = @"C:\Games\A\game.exe",
                    IsEnabled = true,
                    HDREnabled = true
                }
            });
            var hdr = new FakeHdr();
            var play = new FakePlayTime();
            var svc = CreateService(monitor, cfg, hdr, play);

            // Act: 启动两个不同路径的进程（主程序 + 子目录中的副本）
            monitor.RaiseStart(new ProcessEventInfo("game.exe", @"C:\Games\A\game.exe"));
            monitor.RaiseStart(new ProcessEventInfo("game.exe", @"C:\Games\A\bin\game.exe"));

            // Assert: 只计一次开始
            Assert.Equal(1, play.StartCalls);
            Assert.Equal(1, hdr.EnableCalls);

            // Act: 停止第一个
            monitor.RaiseStop(new ProcessEventInfo("game.exe", @"C:\Games\A\game.exe"));

            // Assert: 还不能停止计时（还有第二个活跃）
            Assert.Equal(0, play.StopCalls);
            Assert.Equal(0, hdr.DisableCalls);
            Assert.True(hdr.IsEnabled);

            // Act: 停止第二个
            monitor.RaiseStop(new ProcessEventInfo("game.exe", @"C:\Games\A\bin\game.exe"));

            // Assert: 现在才停止
            Assert.Equal(1, play.StopCalls);
            Assert.Equal(1, hdr.DisableCalls);
            Assert.False(hdr.IsEnabled);
        }

        [Fact]
        public void MultipleProcesses_SameNameDifferentPaths_StopByNameFallback_CleansBothIndexes()
        {
            // Arrange: 同一游戏，启动两个不同路径进程
            var monitor = new FakeMonitor();
            var cfg = new FakeConfig(new Dictionary<string, CoreGameConfig>(StringComparer.OrdinalIgnoreCase)
            {
                ["fallback"] = new()
                {
                    DataKey = "fallback",
                    ExecutableName = "game.exe",
                    ExecutablePath = @"C:\Games\A\game.exe",
                    IsEnabled = true
                }
            });
            var play = new FakePlayTime();
            var svc = CreateService(monitor, cfg, new FakeHdr(), play);

            // 启动两个进程
            monitor.RaiseStart(new ProcessEventInfo("game.exe", @"C:\Games\A\game.exe"));
            monitor.RaiseStart(new ProcessEventInfo("game.exe", @"C:\Games\A\bin\game.exe"));

            // Act: 停止时传入的路径不在 _activeByPath 中（模拟路径信息缺失）
            // 此时应降级到 _activeByName 查找
            monitor.RaiseStop(new ProcessEventInfo("game.exe", null));  // path 为 null

            // Assert: 第一次停止只移除一个
            Assert.Equal(0, play.StopCalls);  // 还有第二个活跃

            // Act: 再次停止（_activeByName 中只剩一个）
            monitor.RaiseStop(new ProcessEventInfo("game.exe", null));

            // Assert: 现在全清完了
            Assert.Equal(1, play.StopCalls);
        }

        #endregion
    }
}
