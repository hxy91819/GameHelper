using System;
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
            string? received = null;
            monitor.ProcessStarted += name => received = name;

            monitor.SimulateStart("game.exe");
            Assert.Equal("game.exe", received);
        }

        [Fact]
        public void SimulateStop_Raises_ProcessStopped()
        {
            var monitor = new NoOpProcessMonitor();
            string? received = null;
            monitor.ProcessStopped += name => received = name;

            monitor.SimulateStop("game.exe");
            Assert.Equal("game.exe", received);
        }
    }
}
