using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using GameHelper.Core.Abstractions;
using GameHelper.Core.Models;
using GameHelper.Core.Services;
using GameHelper.Infrastructure.Providers;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace GameHelper.Tests
{
    public class GameAutomationPlaytimeIntegrationTests : IDisposable
    {
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
            public void Enable() { IsEnabled = true; }
            public void Disable() { IsEnabled = false; }
        }

        private sealed class FakeConfig : IConfigProvider
        {
            private readonly IReadOnlyDictionary<string, GameConfig> _configs;
            public FakeConfig(IReadOnlyDictionary<string, GameConfig> configs) { _configs = configs; }
            public IReadOnlyDictionary<string, GameConfig> Load() => _configs;
            public void Save(IReadOnlyDictionary<string, GameConfig> configs) { }
        }

        private readonly string _dir;
        private readonly string _file;

        public GameAutomationPlaytimeIntegrationTests()
        {
            _dir = Path.Combine(Path.GetTempPath(), "GameHelperTests_Playtime_IT_", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dir);
            _file = Path.Combine(_dir, "playtime.json");
        }

        [Fact]
        public void StartThenStop_PersistsSingleSession()
        {
            var monitor = new FakeMonitor();
            var hdr = new FakeHdr();
            var cfg = new FakeConfig(new Dictionary<string, GameConfig>(StringComparer.OrdinalIgnoreCase)
            {
                ["demo.exe"] = new GameConfig { DataKey = "demo.exe", ExecutableName = "demo.exe", IsEnabled = true, HDREnabled = true }
            });
            var play = new FileBackedPlayTimeService(_dir);
            var svc = new GameAutomationService(monitor, cfg, hdr, play, NullLogger<GameAutomationService>.Instance);

            svc.Start();
            monitor.RaiseStart(new ProcessEventInfo("DEMO.EXE", null)); // case-insensitive
            monitor.RaiseStop(new ProcessEventInfo("demo.exe", null));
            svc.Stop();

            Assert.True(File.Exists(_file));
            using var doc = JsonDocument.Parse(File.ReadAllText(_file));
            var games = doc.RootElement.GetProperty("games");
            var entry = games.EnumerateArray().FirstOrDefault(e => string.Equals(e.GetProperty("GameName").GetString(), "demo.exe", StringComparison.OrdinalIgnoreCase));
            Assert.NotEqual(JsonValueKind.Undefined, entry.ValueKind);
            Assert.Equal(1, entry.GetProperty("Sessions").GetArrayLength());
        }

        [Fact]
        public void StartWithoutStop_FlushOnServiceStop_PersistsSession()
        {
            var monitor = new FakeMonitor();
            var hdr = new FakeHdr();
            var cfg = new FakeConfig(new Dictionary<string, GameConfig>(StringComparer.OrdinalIgnoreCase)
            {
                ["flush.exe"] = new GameConfig { DataKey = "flush.exe", ExecutableName = "flush.exe", IsEnabled = true, HDREnabled = true }
            });
            var play = new FileBackedPlayTimeService(_dir);
            var svc = new GameAutomationService(monitor, cfg, hdr, play, NullLogger<GameAutomationService>.Instance);

            svc.Start();
            monitor.RaiseStart(new ProcessEventInfo("flush.exe", null));
            // Do NOT raise stop; rely on GameAutomationService.Stop() to flush
            svc.Stop();

            Assert.True(File.Exists(_file));
            using var doc = JsonDocument.Parse(File.ReadAllText(_file));
            var games = doc.RootElement.GetProperty("games");
            var entry = games.EnumerateArray().FirstOrDefault(e => string.Equals(e.GetProperty("GameName").GetString(), "flush.exe", StringComparison.OrdinalIgnoreCase));
            Assert.NotEqual(JsonValueKind.Undefined, entry.ValueKind);
            Assert.Equal(1, entry.GetProperty("Sessions").GetArrayLength());
        }

        public void Dispose()
        {
            try { if (Directory.Exists(_dir)) Directory.Delete(_dir, true); } catch { }
        }
    }
}
