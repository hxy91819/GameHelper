using System;
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
    }
}
