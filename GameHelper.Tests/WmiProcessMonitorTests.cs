using System;
using System.Collections.Generic;
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
    }
}
