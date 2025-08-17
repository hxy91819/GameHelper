using System;
using System.Collections.Generic;
using GameHelper.Infrastructure.Processes;
using Xunit;

namespace GameHelper.Tests
{
    public class WmiProcessMonitorTests
    {
        private sealed class FakeWatcher : IProcessEventWatcher
        {
            public event Action<string>? ProcessEvent;
            public bool Started { get; private set; }

            public void Start() => Started = true;
            public void Stop() => Started = false;

            public void Raise(string name) => ProcessEvent?.Invoke(name);
        }

        [Fact]
        public void WmiProcessMonitor_Raises_Events_From_Watchers()
        {
            var start = new FakeWatcher();
            var stop = new FakeWatcher();
            var monitor = new WmiProcessMonitor(start, stop);

            var started = new List<string>();
            var stopped = new List<string>();
            monitor.ProcessStarted += s => started.Add(s);
            monitor.ProcessStopped += s => stopped.Add(s);

            monitor.Start();

            start.Raise("game.exe");
            start.Raise("other.exe");
            stop.Raise("game.exe");

            Assert.Equal(new[] { "game.exe", "other.exe" }, started);
            Assert.Equal(new[] { "game.exe" }, stopped);

            monitor.Stop();

            // After Stop, further events should not be forwarded
            start.Raise("after.exe");
            stop.Raise("after.exe");

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
