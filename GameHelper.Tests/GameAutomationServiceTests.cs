using System;
using System.Collections.Generic;
using System.Linq;
using GameHelper.Core.Abstractions;
using GameHelper.Core.Models;
using GameHelper.Core.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using CoreGameConfig = GameHelper.Core.Models.GameConfig;

namespace GameHelper.Tests
{
    // Fakes
    file sealed class FakeMonitor : IProcessMonitor
    {
        public event Action<ProcessEventInfo>? ProcessStarted;
        public event Action<ProcessEventInfo>? ProcessStopped;
        public void Start() { }
        public void Stop() { }
        public void Dispose() { }
        public void RaiseStart(ProcessEventInfo info) => ProcessStarted?.Invoke(info);
        public void RaiseStop(ProcessEventInfo info) => ProcessStopped?.Invoke(info);
    }

    file sealed class FakeHdr : IHdrController
    {
        public int EnableCalls { get; private set; }
        public int DisableCalls { get; private set; }
        public bool IsEnabled { get; private set; }
        public void Enable() { IsEnabled = true; EnableCalls++; }
        public void Disable() { IsEnabled = false; DisableCalls++; }
        public void SetState(bool enabled) { IsEnabled = enabled; }
    }

    file sealed class FakePlayTime : IPlayTimeService
    {
        public int StartCalls { get; private set; }
        public int StopCalls { get; private set; }
        public List<string> Started { get; } = new();
        public List<string> Stopped { get; } = new();
        public void StartTracking(string gameName) { StartCalls++; Started.Add(gameName); }
        public PlaySession? StopTracking(string gameName)
        {
            StopCalls++;
            Stopped.Add(gameName);
            return new PlaySession(gameName, DateTime.MinValue, DateTime.MinValue, TimeSpan.Zero, 0);
        }
    }

    file sealed class FakePlayTimeWithDuration : IPlayTimeService
    {
        private readonly DateTime _startTime;
        private readonly TimeSpan _duration;

        public FakePlayTimeWithDuration(DateTime startTime, TimeSpan duration)
        {
            _startTime = startTime;
            _duration = duration;
        }

        public void StartTracking(string gameName) { }

        public PlaySession? StopTracking(string gameName)
        {
            var endTime = _startTime + _duration;
            var durationMinutes = (long)Math.Round(_duration.TotalMinutes);
            return new PlaySession(gameName, _startTime, endTime, _duration, durationMinutes);
        }
    }

    file sealed class ListLogger<T> : ILogger<T>
    {
        private sealed class NullScope : IDisposable
        {
            public static NullScope Instance { get; } = new();
            public void Dispose() { }
        }

        public List<(LogLevel Level, string Message)> Entries { get; } = new();

        public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (formatter is null)
            {
                return;
            }

            Entries.Add((logLevel, formatter(state, exception)));
        }
    }

    file sealed class FakeConfig : IConfigProvider
    {
        private readonly IReadOnlyDictionary<string, CoreGameConfig> _configs;
        public FakeConfig(IReadOnlyDictionary<string, CoreGameConfig> configs)
        {
            _configs = configs;
        }
        public IReadOnlyDictionary<string, CoreGameConfig> Load() => _configs;
        public void Save(IReadOnlyDictionary<string, CoreGameConfig> configs) { /* not used in tests */ }
    }

    file sealed class MutableFakeConfig : IConfigProvider
    {
        private IReadOnlyDictionary<string, CoreGameConfig> _configs;

        public MutableFakeConfig(IReadOnlyDictionary<string, CoreGameConfig> initial)
        {
            _configs = initial;
        }

        public IReadOnlyDictionary<string, CoreGameConfig> Load() => _configs;

        public void Save(IReadOnlyDictionary<string, CoreGameConfig> configs)
        {
            _configs = configs;
        }

        public void Set(IReadOnlyDictionary<string, CoreGameConfig> configs)
        {
            _configs = configs;
        }
    }

    public class GameAutomationServiceTests
    {
        private static IReadOnlyDictionary<string, CoreGameConfig> Dict(params (string name, bool enabled, bool hdrEnabled)[] items)
        {
            var dict = new Dictionary<string, CoreGameConfig>(StringComparer.OrdinalIgnoreCase);
            foreach (var (name, enabled, hdrEnabled) in items)
            {
                dict[name] = new CoreGameConfig 
                { 
                    DataKey = name, 
                    ExecutableName = name, 
                    IsEnabled = enabled, 
                    HDREnabled = hdrEnabled 
                };
            }
            return dict;
        }

        [Fact]
        public void SingleGame_Lifecycle_EnablesAndDisablesHdr_TracksPlaytime()
        {
            var monitor = new FakeMonitor();
            var cfg = new FakeConfig(Dict(("witcher3.exe", true, true)));
            var hdr = new FakeHdr();
            var play = new FakePlayTime();
            var logger = NullLogger<GameAutomationService>.Instance;
            var svc = new GameAutomationService(monitor, cfg, hdr, play, logger);

            svc.Start();

            // Case-insensitive start
            monitor.RaiseStart(new ProcessEventInfo("WITCHER3.EXE", null));
            Assert.Equal(1, hdr.EnableCalls);
            Assert.True(hdr.IsEnabled);
            Assert.Equal(1, play.StartCalls);
            Assert.Contains(play.Started, s => string.Equals(s, "witcher3.exe", StringComparison.OrdinalIgnoreCase));

            // Stop -> disable when last game exits
            monitor.RaiseStop(new ProcessEventInfo("witcher3.exe", null));
            Assert.Equal(1, play.StopCalls);
            Assert.Equal(1, hdr.DisableCalls);
            Assert.False(hdr.IsEnabled);
        }

        [Fact]
        public void MultipleGames_HdrOnlyOnFirstAndLast()
        {
            var monitor = new FakeMonitor();
            var cfg = new FakeConfig(Dict(("a.exe", true, true), ("b.exe", true, true)));
            var hdr = new FakeHdr();
            var play = new FakePlayTime();
            var logger = NullLogger<GameAutomationService>.Instance;
            var svc = new GameAutomationService(monitor, cfg, hdr, play, logger);

            svc.Start();

            monitor.RaiseStart(new ProcessEventInfo("a.exe", null));
            Assert.Equal(1, hdr.EnableCalls);
            Assert.True(hdr.IsEnabled);

            monitor.RaiseStart(new ProcessEventInfo("b.exe", null));
            // still only once
            Assert.Equal(1, hdr.EnableCalls);
            Assert.True(hdr.IsEnabled);

            monitor.RaiseStop(new ProcessEventInfo("a.exe", null));
            // still enabled due to b.exe
            Assert.Equal(0, hdr.DisableCalls);
            Assert.True(hdr.IsEnabled);

            monitor.RaiseStop(new ProcessEventInfo("b.exe", null));
            // last one -> disable
            Assert.Equal(1, hdr.DisableCalls);
            Assert.False(hdr.IsEnabled);
        }

        [Fact]
        public void GameConfiguredToDisableHdr_TogglesOffIfHdrWasEnabled()
        {
            var monitor = new FakeMonitor();
            var cfg = new FakeConfig(Dict(("sdr.exe", true, false)));
            var hdr = new FakeHdr();
            hdr.SetState(true); // simulate HDR already enabled before the game starts
            var play = new FakePlayTime();
            var logger = NullLogger<GameAutomationService>.Instance;
            var svc = new GameAutomationService(monitor, cfg, hdr, play, logger);

            svc.Start();

            monitor.RaiseStart(new ProcessEventInfo("sdr.exe", null));

            Assert.Equal(1, hdr.DisableCalls);
            Assert.False(hdr.IsEnabled);

            monitor.RaiseStop(new ProcessEventInfo("sdr.exe", null));
            Assert.Equal(1, hdr.DisableCalls);
        }

        [Fact]
        public void MixedHdrPreferences_OnlyEnablesWhenRequested()
        {
            var monitor = new FakeMonitor();
            var cfg = new FakeConfig(Dict(("hdr.exe", true, true), ("sdr.exe", true, false)));
            var hdr = new FakeHdr();
            var play = new FakePlayTime();
            var logger = NullLogger<GameAutomationService>.Instance;
            var svc = new GameAutomationService(monitor, cfg, hdr, play, logger);

            svc.Start();

            monitor.RaiseStart(new ProcessEventInfo("sdr.exe", null));
            Assert.Equal(0, hdr.EnableCalls);
            Assert.False(hdr.IsEnabled);

            monitor.RaiseStart(new ProcessEventInfo("hdr.exe", null));
            Assert.Equal(1, hdr.EnableCalls);
            Assert.True(hdr.IsEnabled);

            monitor.RaiseStop(new ProcessEventInfo("hdr.exe", null));
            Assert.Equal(1, hdr.DisableCalls);
            Assert.False(hdr.IsEnabled);

            monitor.RaiseStop(new ProcessEventInfo("sdr.exe", null));
            Assert.Equal(1, hdr.DisableCalls);
        }

        [Fact]
        public void DisabledGame_Ignored()
        {
            var monitor = new FakeMonitor();
            var cfg = new FakeConfig(Dict(("c.exe", false, true)));
            var hdr = new FakeHdr();
            var play = new FakePlayTime();
            var logger = NullLogger<GameAutomationService>.Instance;
            var svc = new GameAutomationService(monitor, cfg, hdr, play, logger);

            svc.Start();

            monitor.RaiseStart(new ProcessEventInfo("c.exe", null));
            monitor.RaiseStop(new ProcessEventInfo("c.exe", null));

            Assert.Equal(0, hdr.EnableCalls);
            Assert.Equal(0, hdr.DisableCalls);
            Assert.Equal(0, play.StartCalls);
            Assert.Equal(0, play.StopCalls);
        }

        [Fact]
        public void Stop_Unsubscribes_EventsNoLongerProcessed()
        {
            var monitor = new FakeMonitor();
            var cfg = new FakeConfig(Dict(("a.exe", true, true)));
            var hdr = new FakeHdr();
            var play = new FakePlayTime();
            var logger = NullLogger<GameAutomationService>.Instance;
            var svc = new GameAutomationService(monitor, cfg, hdr, play, logger);

            svc.Start();
            monitor.RaiseStart(new ProcessEventInfo("a.exe", null));
            Assert.Equal(1, hdr.EnableCalls);

            svc.Stop();

            // After Stop, further events should not be handled
            monitor.RaiseStop(new ProcessEventInfo("a.exe", null));
            monitor.RaiseStart(new ProcessEventInfo("a.exe", null));

            Assert.Equal(1, hdr.EnableCalls);
            Assert.Equal(0, hdr.DisableCalls); // not processed after Stop
        }

        [Fact]
        public void StopEvent_LogsFormattedDurationIncludingSeconds()
        {
            var monitor = new FakeMonitor();
            var cfg = new FakeConfig(Dict(("game.exe", true, true)));
            var hdr = new FakeHdr();
            var play = new FakePlayTimeWithDuration(
                DateTime.SpecifyKind(new DateTime(2025, 1, 1, 8, 0, 0), DateTimeKind.Local),
                TimeSpan.FromSeconds(75));
            var logger = new ListLogger<GameAutomationService>();
            var svc = new GameAutomationService(monitor, cfg, hdr, play, logger);

            svc.Start();
            monitor.RaiseStart(new ProcessEventInfo("game.exe", null));
            monitor.RaiseStop(new ProcessEventInfo("game.exe", null));

            var entry = Assert.Single(logger.Entries, e => e.Level == LogLevel.Information && e.Message.Contains("游玩时长"));
            Assert.Contains("15秒", entry.Message);
        }

        [Fact]
        public void ReloadConfig_ShouldRefreshPathAndNameIndexes_ForNewProcesses()
        {
            var monitor = new FakeMonitor();
            var cfg = new MutableFakeConfig(Dict(("old.exe", true, true)));
            var hdr = new FakeHdr();
            var play = new FakePlayTime();
            var logger = NullLogger<GameAutomationService>.Instance;
            var svc = new GameAutomationService(monitor, cfg, hdr, play, logger);

            svc.Start();
            monitor.RaiseStart(new ProcessEventInfo("new.exe", @"C:\Games\new.exe"));
            Assert.Equal(0, play.StartCalls);

            cfg.Set(new Dictionary<string, CoreGameConfig>(StringComparer.OrdinalIgnoreCase)
            {
                ["new.exe"] = new()
                {
                    DataKey = "new-key",
                    ExecutableName = "new.exe",
                    ExecutablePath = @"C:\Games\new.exe",
                    IsEnabled = true,
                    HDREnabled = true
                }
            });

            svc.ReloadConfig();

            monitor.RaiseStart(new ProcessEventInfo("new.exe", @"C:\Games\new.exe"));
            Assert.Equal(1, play.StartCalls);
            Assert.Contains("new-key", play.Started, StringComparer.OrdinalIgnoreCase);
        }

        [Fact]
        public void ReloadConfig_ShouldNotBreakActiveTrackingState()
        {
            var monitor = new FakeMonitor();
            var cfg = new MutableFakeConfig(Dict(("game.exe", true, true)));
            var hdr = new FakeHdr();
            var play = new FakePlayTime();
            var logger = NullLogger<GameAutomationService>.Instance;
            var svc = new GameAutomationService(monitor, cfg, hdr, play, logger);

            svc.Start();
            monitor.RaiseStart(new ProcessEventInfo("game.exe", null));
            Assert.Equal(1, play.StartCalls);

            svc.ReloadConfig();
            monitor.RaiseStop(new ProcessEventInfo("game.exe", null));

            Assert.Equal(1, play.StopCalls);
        }
    }
}

