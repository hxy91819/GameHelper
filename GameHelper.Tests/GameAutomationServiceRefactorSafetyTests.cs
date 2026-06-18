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
    public class GameAutomationServiceRefactorSafetyTests
    {

        private sealed class FakeMonitor : IProcessMonitor, IProcessNameFilterControl
        {
            public event Action<ProcessEventInfo>? ProcessStarted;
            public event Action<ProcessEventInfo>? ProcessStopped;
            public void Start() { }
            public void Stop() { }
            public void Dispose() { }
            public void SetAllowedProcessNames(IEnumerable<string> processNames) { }
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

        private sealed class FakePathResolver : IProcessPathResolver
        {
            private readonly Dictionary<int, string> _paths = new();
            public void SetPath(int processId, string path) => _paths[processId] = path;
            public string? TryResolveExecutablePath(int processId) =>
                _paths.TryGetValue(processId, out var path) ? path : null;
        }

        private static void CreateService(
            IProcessMonitor monitor,
            IConfigProvider cfg,
            IHdrController hdr,
            IPlayTimeService play,
            ILogger<GameAutomationService>? logger = null,
            IProcessPathResolver? pathResolver = null)
        {
            var svc = new GameAutomationService(
                monitor,
                cfg,
                hdr,
                play,
                logger ?? NullLogger<GameAutomationService>.Instance,
                pathResolver);
            svc.Start();
        }


        [Fact]
        public void SameDataKey_MultipleProcesses_RefCounting_TracksOnce()
        {
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
            CreateService(monitor, cfg, hdr, play);

            monitor.RaiseStart(new ProcessEventInfo("game.exe", @"C:\Games\game.exe"));
            monitor.RaiseStart(new ProcessEventInfo("game.exe", @"C:\Games\game.exe"));
            monitor.RaiseStart(new ProcessEventInfo("game.exe", @"C:\Games\game.exe"));

            Assert.Equal(1, play.StartCalls);
            Assert.Equal(1, hdr.EnableCalls);
            Assert.True(hdr.IsEnabled);

            monitor.RaiseStop(new ProcessEventInfo("game.exe", @"C:\Games\game.exe"));
            monitor.RaiseStop(new ProcessEventInfo("game.exe", @"C:\Games\game.exe"));

            Assert.Equal(0, play.StopCalls);
            Assert.Equal(0, hdr.DisableCalls);
            Assert.True(hdr.IsEnabled);

            monitor.RaiseStop(new ProcessEventInfo("game.exe", @"C:\Games\game.exe"));

            Assert.Equal(1, play.StopCalls);
            Assert.Equal(1, hdr.DisableCalls);
            Assert.False(hdr.IsEnabled);
        }



        [Fact]
        public void L1_ExactPathMatch_BypassesFuzzyMatching()
        {
            var monitor = new FakeMonitor();
            var cfg = new FakeConfig(new Dictionary<string, CoreGameConfig>(StringComparer.OrdinalIgnoreCase)
            {
                ["exact"] = new()
                {
                    DataKey = "exact",
                    ExecutableName = "something-else.exe",
                    ExecutablePath = @"C:\Steam\game.exe",
                    IsEnabled = true,
                    HDREnabled = false
                }
            });
            var hdr = new FakeHdr();
            var play = new FakePlayTime();
            CreateService(monitor, cfg, hdr, play);

            monitor.RaiseStart(new ProcessEventInfo("game.exe", @"C:\Steam\game.exe"));

            Assert.Equal(1, play.StartCalls);
            Assert.Contains(play.Started, s => string.Equals(s, "exact", StringComparison.OrdinalIgnoreCase));
        }

        [Fact]
        public void L1_PathHintMatch_AllowsNonCandidateEventName()
        {
            var monitor = new FakeMonitor();
            var cfg = new FakeConfig(new Dictionary<string, CoreGameConfig>(StringComparer.OrdinalIgnoreCase)
            {
                ["exact"] = new()
                {
                    DataKey = "exact",
                    ExecutableName = "game.exe",
                    ExecutablePath = @"C:\Steam\game.exe",
                    IsEnabled = true,
                    HDREnabled = false
                }
            });
            var play = new FakePlayTime();
            CreateService(monitor, cfg, new FakeHdr(), play);

            monitor.RaiseStart(new ProcessEventInfo("launcher.exe", @"C:\Steam\game.exe"));

            Assert.Equal(1, play.StartCalls);
            Assert.Contains(play.Started, s => string.Equals(s, "exact", StringComparison.OrdinalIgnoreCase));
        }

        [Fact]
        public void L1_ResolvedPidPath_AllowsNonCandidateEventName()
        {
            var monitor = new FakeMonitor();
            var cfg = new FakeConfig(new Dictionary<string, CoreGameConfig>(StringComparer.OrdinalIgnoreCase)
            {
                ["exact"] = new()
                {
                    DataKey = "exact",
                    ExecutableName = "game.exe",
                    ExecutablePath = @"C:\Steam\game.exe",
                    IsEnabled = true,
                    HDREnabled = false
                }
            });
            var resolver = new FakePathResolver();
            resolver.SetPath(42, @"C:\Steam\game.exe");
            var play = new FakePlayTime();
            CreateService(monitor, cfg, new FakeHdr(), play, pathResolver: resolver);

            monitor.RaiseStart(new ProcessEventInfo("launcher.exe", null, 42));

            Assert.Equal(1, play.StartCalls);
            Assert.Contains(play.Started, s => string.Equals(s, "exact", StringComparison.OrdinalIgnoreCase));
        }

        [Fact]
        public void L1_PathOnlyConfig_DoesNotFallbackToFilename_WhenPathUnavailable()
        {
            var monitor = new FakeMonitor();
            var cfg = new FakeConfig(new Dictionary<string, CoreGameConfig>(StringComparer.OrdinalIgnoreCase)
            {
                ["path-only"] = new()
                {
                    DataKey = "path-only",
                    ExecutablePath = @"C:\Steam\game.exe",
                    IsEnabled = true,
                    HDREnabled = false
                }
            });
            var play = new FakePlayTime();
            CreateService(monitor, cfg, new FakeHdr(), play);

            monitor.RaiseStart(new ProcessEventInfo("game.exe", null));

            Assert.Equal(0, play.StartCalls);
        }

        [Theory]
        [InlineData(@"C:\Steam\game.exe", @"C:\Steam\game.exe")]
        [InlineData(@"C:\Steam\game.exe\", @"C:\Steam\game.exe")]
        [InlineData(@"c:\steam\game.exe", @"C:\Steam\game.exe")]
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
            CreateService(monitor, cfg, new FakeHdr(), play);

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
            CreateService(monitor, cfg, new FakeHdr(), play);

            monitor.RaiseStart(new ProcessEventInfo("game.exe", @"C:\Steam\game.exe"));

            Assert.Equal(1, play.StartCalls);
            Assert.Contains("first", play.Started);
            Assert.DoesNotContain("second", play.Started);
        }



        [Fact]
        public void AmbiguousName_SameExecutableNameDifferentPaths_PathRelatednessResolves()
        {
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
            CreateService(monitor, cfg, new FakeHdr(), play);

            monitor.RaiseStart(new ProcessEventInfo("game.exe", @"C:\Games\A\bin\game.exe"));

            Assert.Equal(1, play.StartCalls);
            Assert.Contains("gameA", play.Started);
            Assert.DoesNotContain("gameB", play.Started);
        }

        [Fact]
        public void AmbiguousName_NoPathRelatedness_AllRejected()
        {
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
            CreateService(monitor, cfg, new FakeHdr(), play);

            monitor.RaiseStart(new ProcessEventInfo("game.exe", @"D:\Random\game.exe"));

            Assert.Equal(0, play.StartCalls);
        }


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
            CreateService(monitor, cfg, new FakeHdr(), play);

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
            CreateService(monitor, cfg, new FakeHdr(), play);

            monitor.RaiseStart(new ProcessEventInfo("game.exe", "game.exe"));

            Assert.Equal(1, play.StartCalls);
        }


        [Fact]
        public void MultipleProcesses_DifferentPaths_StopOne_KeepsOtherActive()
        {
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
            CreateService(monitor, cfg, hdr, play);

            monitor.RaiseStart(new ProcessEventInfo("game.exe", @"C:\Games\A\game.exe"));
            monitor.RaiseStart(new ProcessEventInfo("game.exe", @"C:\Games\A\bin\game.exe"));

            Assert.Equal(1, play.StartCalls);
            Assert.Equal(1, hdr.EnableCalls);

            monitor.RaiseStop(new ProcessEventInfo("game.exe", @"C:\Games\A\game.exe"));

            Assert.Equal(0, play.StopCalls);
            Assert.Equal(0, hdr.DisableCalls);
            Assert.True(hdr.IsEnabled);

            monitor.RaiseStop(new ProcessEventInfo("game.exe", @"C:\Games\A\bin\game.exe"));

            Assert.Equal(1, play.StopCalls);
            Assert.Equal(1, hdr.DisableCalls);
            Assert.False(hdr.IsEnabled);
        }

        [Fact]
        public void MultipleProcesses_SameNameDifferentPaths_StopByNameFallback_CleansBothIndexes()
        {
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
            CreateService(monitor, cfg, new FakeHdr(), play);

            monitor.RaiseStart(new ProcessEventInfo("game.exe", @"C:\Games\A\game.exe"));
            monitor.RaiseStart(new ProcessEventInfo("game.exe", @"C:\Games\A\bin\game.exe"));

            monitor.RaiseStop(new ProcessEventInfo("game.exe", null));

            Assert.Equal(0, play.StopCalls);
            monitor.RaiseStop(new ProcessEventInfo("game.exe", null));

            Assert.Equal(1, play.StopCalls);
        }

    }
}

