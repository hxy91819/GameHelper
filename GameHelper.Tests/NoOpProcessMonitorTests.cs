using System;
using GameHelper.Core.Abstractions;
using GameHelper.Core.Models;
using GameHelper.Infrastructure.Processes;
using Xunit;

namespace GameHelper.Tests
{
    public class NoOpProcessMonitorTests
    {
        [Fact]
        public void Start_Stop_DoNotThrow()
        {
            var monitor = new NoOpProcessMonitor();
            var ex1 = Record.Exception(() => monitor.Start());
            var ex2 = Record.Exception(() => monitor.Stop());
            Assert.Null(ex1);
            Assert.Null(ex2);
        }

        [Fact]
        public void SimulateStart_Raises_ProcessStarted()
        {
            var monitor = new NoOpProcessMonitor();
            ProcessEventInfo? received = null;
            monitor.ProcessStarted += info => received = info;

            monitor.SimulateStart(new ProcessEventInfo("game.exe", null));
            Assert.Equal("game.exe", received?.ExecutableName);
        }

        [Fact]
        public void SimulateStop_Raises_ProcessStopped()
        {
            var monitor = new NoOpProcessMonitor();
            ProcessEventInfo? received = null;
            monitor.ProcessStopped += info => received = info;

            monitor.SimulateStop(new ProcessEventInfo("game.exe", null));
            Assert.Equal("game.exe", received?.ExecutableName);
        }

        [Fact]
        public void Configure_AppliesCandidateAndStopEventContract()
        {
            var monitor = new NoOpProcessMonitor();
            var started = new List<string>();
            var stopped = new List<string>();
            monitor.ProcessStarted += info => started.Add(info.ExecutableName);
            monitor.ProcessStopped += info => stopped.Add(info.ExecutableName);

            monitor.Configure(new ProcessObservationPolicy(new[] { "GAME" }, observeStopEvents: false));
            monitor.SimulateStart(new ProcessEventInfo("game.exe", null));
            monitor.SimulateStart(new ProcessEventInfo("other.exe", null));
            monitor.SimulateStop(new ProcessEventInfo("game.exe", null));

            Assert.Equal(new[] { "game.exe" }, started);
            Assert.Empty(stopped);

            monitor.Configure(new ProcessObservationPolicy(new[] { "game.exe" }, observeStopEvents: true));
            monitor.SimulateStop(new ProcessEventInfo("game.exe", null));
            monitor.SimulateStop(new ProcessEventInfo("other.exe", null));

            Assert.Equal(new[] { "game.exe" }, stopped);
        }
    }
}
