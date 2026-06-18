using System;
using System.Collections.Generic;
using GameHelper.Core.Abstractions;
using GameHelper.Core.Models;
using GameHelper.Infrastructure.Processes;
using Xunit;

namespace GameHelper.Tests
{
    public class WmiProcessMonitorTests
    {
        private sealed class FakeWatcher : IProcessEventWatcher
        {
            public event Action<ProcessEventInfo>? ProcessEvent;
            public bool Started { get; private set; }

            public void Start() => Started = true;
            public void Stop() => Started = false;

            public void Raise(ProcessEventInfo info) => ProcessEvent?.Invoke(info);
        }

        [Fact]
        public void WmiProcessMonitor_Raises_Events_From_Watchers()
        {
            var start = new FakeWatcher();
            var stop = new FakeWatcher();
            var monitor = new WmiProcessMonitor(start, stop);

            var started = new List<string>();
            var stopped = new List<string>();
            monitor.ProcessStarted += info => started.Add(info.ExecutableName);
            monitor.ProcessStopped += info => stopped.Add(info.ExecutableName);

            monitor.Start();

            start.Raise(new ProcessEventInfo("game.exe", null));
            start.Raise(new ProcessEventInfo("other.exe", null));
            stop.Raise(new ProcessEventInfo("game.exe", null));

            Assert.Equal(new[] { "game.exe", "other.exe" }, started);
            Assert.Equal(new[] { "game.exe" }, stopped);

            monitor.Stop();

            // After Stop, further events should not be forwarded
            start.Raise(new ProcessEventInfo("after.exe", null));
            stop.Raise(new ProcessEventInfo("after.exe", null));

            Assert.DoesNotContain("after.exe", started);
            Assert.DoesNotContain("after.exe", stopped);
        }

        [Fact]
        public void WmiProcessMonitor_Filters_ExternalWatcherEvents_WhenAllowedNamesAreSet()
        {
            var start = new FakeWatcher();
            var stop = new FakeWatcher();
            var monitor = new WmiProcessMonitor(start, stop);
            ((IProcessNameFilterControl)monitor).SetAllowedProcessNames(new[] { "game.exe" });

            var started = new List<string>();
            var stopped = new List<string>();
            monitor.ProcessStarted += info => started.Add(info.ExecutableName);
            monitor.ProcessStopped += info => stopped.Add(info.ExecutableName);

            monitor.Start();

            start.Raise(new ProcessEventInfo("game.exe", null));
            start.Raise(new ProcessEventInfo("other.exe", null));
            stop.Raise(new ProcessEventInfo("game.exe", null));
            stop.Raise(new ProcessEventInfo("other.exe", null));

            Assert.Equal(new[] { "game.exe" }, started);
            Assert.Equal(new[] { "game.exe" }, stopped);
        }

        [Fact]
        public void WmiProcessMonitor_Start_Stop_Idempotent()
        {
            var monitor = new WmiProcessMonitor(new FakeWatcher(), new FakeWatcher());

            monitor.Start();
            monitor.Start(); // should not throw
            monitor.Stop();
            monitor.Stop(); // should not throw

            monitor.Dispose();
            // Dispose again safely via using pattern
            monitor.Dispose();
        }

        [Fact]
        public void WmiProcessEventResolver_Skips_NonCandidateStart_BeforeDetailsLookup()
        {
            var lookupCount = 0;
            var resolver = new WmiProcessEventResolver(
                name => string.Equals(name, "game.exe", StringComparison.OrdinalIgnoreCase),
                pid =>
                {
                    lookupCount++;
                    return new WmiProcessDetails("game.exe", @"C:\Games\game.exe");
                });

            var created = resolver.TryCreateProcessEvent(
                "Win32_ProcessStartTrace",
                123,
                "other.exe",
                out _);

            Assert.False(created);
            Assert.Equal(0, lookupCount);
        }

        [Fact]
        public void WmiProcessEventResolver_ResolvesDetails_OnlyForCandidateStart()
        {
            var lookupCount = 0;
            var resolver = new WmiProcessEventResolver(
                name => string.Equals(name, "game.exe", StringComparison.OrdinalIgnoreCase),
                pid =>
                {
                    lookupCount++;
                    return new WmiProcessDetails("game.exe", @"C:\Games\game.exe");
                });

            var created = resolver.TryCreateProcessEvent(
                "Win32_ProcessStartTrace",
                123,
                "game.exe",
                out var info);

            Assert.True(created);
            Assert.Equal(1, lookupCount);
            Assert.Equal("game.exe", info.ExecutableName);
            Assert.Equal(@"C:\Games\game.exe", info.ExecutablePath);
            Assert.Equal(123, info.ProcessId);
        }

        [Fact]
        public void WmiProcessEventResolver_Stop_ReusesCachedStartDetails()
        {
            var resolver = new WmiProcessEventResolver(
                name => string.Equals(name, "game.exe", StringComparison.OrdinalIgnoreCase),
                _ => new WmiProcessDetails("game.exe", @"C:\Games\game.exe"));

            var startCreated = resolver.TryCreateProcessEvent(
                "Win32_ProcessStartTrace",
                123,
                "game.exe",
                out _);
            var stopCreated = resolver.TryCreateProcessEvent(
                "Win32_ProcessStopTrace",
                123,
                "game.exe",
                out var stopInfo);

            Assert.True(startCreated);
            Assert.True(stopCreated);
            Assert.Equal("game.exe", stopInfo.ExecutableName);
            Assert.Equal(@"C:\Games\game.exe", stopInfo.ExecutablePath);
            Assert.Equal(123, stopInfo.ProcessId);
        }

        [Fact]
        public void WmiProcessEventResolver_Stop_DoesNotUseCachedDetails_WhenRawNameIsMissing()
        {
            var resolver = new WmiProcessEventResolver(
                name => string.Equals(name, "game.exe", StringComparison.OrdinalIgnoreCase),
                _ => new WmiProcessDetails("game.exe", @"C:\Games\game.exe"));

            var startCreated = resolver.TryCreateProcessEvent(
                "Win32_ProcessStartTrace",
                123,
                "game.exe",
                out _);
            var stopCreated = resolver.TryCreateProcessEvent(
                "Win32_ProcessStopTrace",
                123,
                null,
                out _);

            Assert.True(startCreated);
            Assert.False(stopCreated);
        }

        [Fact]
        public void WmiProcessEventResolver_Stop_DoesNotUseStaleCacheForDifferentNonCandidateName()
        {
            var resolver = new WmiProcessEventResolver(
                name => string.Equals(name, "game.exe", StringComparison.OrdinalIgnoreCase),
                _ => new WmiProcessDetails("game.exe", @"C:\Games\game.exe"));

            var startCreated = resolver.TryCreateProcessEvent(
                "Win32_ProcessStartTrace",
                123,
                "game.exe",
                out _);
            var stopCreated = resolver.TryCreateProcessEvent(
                "Win32_ProcessStopTrace",
                123,
                "other.exe",
                out _);

            Assert.True(startCreated);
            Assert.False(stopCreated);
        }

        [Fact]
        public void WmiProcessEventResolver_Start_DoesNotCache_WhenStopEventsAreDisabled()
        {
            var resolver = new WmiProcessEventResolver(
                name => string.Equals(name, "game.exe", StringComparison.OrdinalIgnoreCase),
                () => false,
                _ => new WmiProcessDetails("game.exe", @"C:\Games\game.exe"));

            var startCreated = resolver.TryCreateProcessEvent(
                "Win32_ProcessStartTrace",
                123,
                "game.exe",
                out _);
            var stopCreated = resolver.TryCreateProcessEvent(
                "Win32_ProcessStopTrace",
                123,
                null,
                out _);

            Assert.True(startCreated);
            Assert.False(stopCreated);
        }

        [Fact]
        public void WmiProcessEventResolver_Start_WithMissingRawName_UsesResolvedCandidateDetails()
        {
            var lookupCount = 0;
            var resolver = new WmiProcessEventResolver(
                name => string.Equals(name, "game.exe", StringComparison.OrdinalIgnoreCase),
                pid =>
                {
                    lookupCount++;
                    return new WmiProcessDetails("game.exe", @"C:\Games\game.exe");
                });

            var created = resolver.TryCreateProcessEvent(
                "Win32_ProcessStartTrace",
                123,
                null,
                out var info);

            Assert.True(created);
            Assert.Equal(1, lookupCount);
            Assert.Equal("game.exe", info.ExecutableName);
            Assert.Equal(@"C:\Games\game.exe", info.ExecutablePath);
        }

        [Fact]
        public void WmiProcessEventResolver_Start_WithMissingRawName_DropsResolvedNonCandidateDetails()
        {
            var lookupCount = 0;
            var resolver = new WmiProcessEventResolver(
                name => string.Equals(name, "game.exe", StringComparison.OrdinalIgnoreCase),
                pid =>
                {
                    lookupCount++;
                    return new WmiProcessDetails("other.exe", @"C:\Tools\other.exe");
                });

            var created = resolver.TryCreateProcessEvent(
                "Win32_ProcessStartTrace",
                123,
                null,
                out _);

            Assert.False(created);
            Assert.Equal(1, lookupCount);
        }
    }
}
