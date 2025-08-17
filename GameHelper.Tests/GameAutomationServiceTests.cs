using System;
using System.Collections.Generic;
using System.Linq;
using GameHelper.Core.Abstractions;
using GameHelper.Core.Models;
using GameHelper.Core.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace GameHelper.Tests
{
    // Fakes
    file sealed class FakeMonitor : IProcessMonitor
    {
        public event Action<string>? ProcessStarted;
        public event Action<string>? ProcessStopped;
        public void Start() { }
        public void Stop() { }
        public void RaiseStart(string name) => ProcessStarted?.Invoke(name);
        public void RaiseStop(string name) => ProcessStopped?.Invoke(name);
    }

    file sealed class FakeHdr : IHdrController
    {
        public int EnableCalls { get; private set; }
        public int DisableCalls { get; private set; }
        public bool IsEnabled { get; private set; }
        public void Enable() { IsEnabled = true; EnableCalls++; }
        public void Disable() { IsEnabled = false; DisableCalls++; }
    }

    file sealed class FakePlayTime : IPlayTimeService
    {
        public int StartCalls { get; private set; }
        public int StopCalls { get; private set; }
        public List<string> Started { get; } = new();
        public List<string> Stopped { get; } = new();
        public void StartTracking(string gameName) { StartCalls++; Started.Add(gameName); }
        public void StopTracking(string gameName) { StopCalls++; Stopped.Add(gameName); }
    }

    file sealed class FakeConfig : IConfigProvider
    {
        private readonly IReadOnlyDictionary<string, GameConfig> _configs;
        public FakeConfig(IReadOnlyDictionary<string, GameConfig> configs)
        {
            _configs = configs;
        }
        public IReadOnlyDictionary<string, GameConfig> Load() => _configs;
        public void Save(IReadOnlyDictionary<string, GameConfig> configs) { /* not used in tests */ }
    }

    public class GameAutomationServiceTests
    {
        private static IReadOnlyDictionary<string, GameConfig> Dict(params (string name, bool enabled)[] items)
        {
            var dict = new Dictionary<string, GameConfig>(StringComparer.OrdinalIgnoreCase);
            foreach (var (name, enabled) in items)
            {
                dict[name] = new GameConfig { Name = name, IsEnabled = enabled, HDREnabled = true };
            }
            return dict;
        }

        [Fact]
        public void SingleGame_Lifecycle_EnablesAndDisablesHdr_TracksPlaytime()
        {
            var monitor = new FakeMonitor();
            var cfg = new FakeConfig(Dict(("witcher3.exe", true)));
            var hdr = new FakeHdr();
            var play = new FakePlayTime();
            var logger = NullLogger<GameAutomationService>.Instance;
            var svc = new GameAutomationService(monitor, cfg, hdr, play, logger);

            svc.Start();

            // Case-insensitive start
            monitor.RaiseStart("WITCHER3.EXE");
            Assert.Equal(1, hdr.EnableCalls);
            Assert.True(hdr.IsEnabled);
            Assert.Equal(1, play.StartCalls);
            Assert.Contains(play.Started, s => string.Equals(s, "WITCHER3.EXE", StringComparison.OrdinalIgnoreCase));

            // Stop -> disable when last game exits
            monitor.RaiseStop("witcher3.exe");
            Assert.Equal(1, play.StopCalls);
            Assert.Equal(1, hdr.DisableCalls);
            Assert.False(hdr.IsEnabled);
        }

        [Fact]
        public void MultipleGames_HdrOnlyOnFirstAndLast()
        {
            var monitor = new FakeMonitor();
            var cfg = new FakeConfig(Dict(("a.exe", true), ("b.exe", true)));
            var hdr = new FakeHdr();
            var play = new FakePlayTime();
            var logger = NullLogger<GameAutomationService>.Instance;
            var svc = new GameAutomationService(monitor, cfg, hdr, play, logger);

            svc.Start();

            monitor.RaiseStart("a.exe");
            Assert.Equal(1, hdr.EnableCalls);
            Assert.True(hdr.IsEnabled);

            monitor.RaiseStart("b.exe");
            // still only once
            Assert.Equal(1, hdr.EnableCalls);
            Assert.True(hdr.IsEnabled);

            monitor.RaiseStop("a.exe");
            // still enabled due to b.exe
            Assert.Equal(0, hdr.DisableCalls);
            Assert.True(hdr.IsEnabled);

            monitor.RaiseStop("b.exe");
            // last one -> disable
            Assert.Equal(1, hdr.DisableCalls);
            Assert.False(hdr.IsEnabled);
        }

        [Fact]
        public void DisabledGame_Ignored()
        {
            var monitor = new FakeMonitor();
            var cfg = new FakeConfig(Dict(("c.exe", false)));
            var hdr = new FakeHdr();
            var play = new FakePlayTime();
            var logger = NullLogger<GameAutomationService>.Instance;
            var svc = new GameAutomationService(monitor, cfg, hdr, play, logger);

            svc.Start();

            monitor.RaiseStart("c.exe");
            monitor.RaiseStop("c.exe");

            Assert.Equal(0, hdr.EnableCalls);
            Assert.Equal(0, hdr.DisableCalls);
            Assert.Equal(0, play.StartCalls);
            Assert.Equal(0, play.StopCalls);
        }

        [Fact]
        public void Stop_Unsubscribes_EventsNoLongerProcessed()
        {
            var monitor = new FakeMonitor();
            var cfg = new FakeConfig(Dict(("a.exe", true)));
            var hdr = new FakeHdr();
            var play = new FakePlayTime();
            var logger = NullLogger<GameAutomationService>.Instance;
            var svc = new GameAutomationService(monitor, cfg, hdr, play, logger);

            svc.Start();
            monitor.RaiseStart("a.exe");
            Assert.Equal(1, hdr.EnableCalls);

            svc.Stop();

            // After Stop, further events should not be handled
            monitor.RaiseStop("a.exe");
            monitor.RaiseStart("a.exe");

            Assert.Equal(1, hdr.EnableCalls);
            Assert.Equal(0, hdr.DisableCalls); // not processed after Stop
        }
    }
}
