using System;
using GameHelper.Core.Abstractions;
using GameHelper.Core.Services;
using Moq;
using Xunit;

namespace GameHelper.Tests
{
    public class MonitorControlServiceTests
    {
        [Fact]
        public void Start_WhenMonitorThrows_RollsBackAutomationAndDoesNotSetIsRunning()
        {
            var monitor = new Mock<IProcessMonitor>();
            var automation = new Mock<IGameAutomationService>();

            monitor.Setup(m => m.Start()).Throws(new InvalidOperationException("ETW fail"));

            var service = new MonitorControlService(monitor.Object, automation.Object);

            Assert.False(service.IsRunning);
            Assert.Throws<InvalidOperationException>(() => service.Start());

            Assert.False(service.IsRunning);
            automation.Verify(a => a.Start(), Times.Once);
            automation.Verify(a => a.Stop(), Times.Once);
        }

        [Fact]
        public void Start_AfterFailedStart_SubsequentStartWorksCleanly()
        {
            var monitor = new Mock<IProcessMonitor>();
            var automation = new Mock<IGameAutomationService>();

            var callCount = 0;
            monitor.Setup(m => m.Start()).Callback(() =>
            {
                callCount++;
                if (callCount == 1)
                    throw new InvalidOperationException("fail");
            });

            var service = new MonitorControlService(monitor.Object, automation.Object);

            Assert.Throws<InvalidOperationException>(() => service.Start());
            Assert.False(service.IsRunning);

            service.Start();
            Assert.True(service.IsRunning);
            automation.Verify(a => a.Start(), Times.Exactly(2));
            automation.Verify(a => a.Stop(), Times.Once);
        }

        [Fact]
        public void Stop_WhenMonitorThrows_StopsAutomationAndSetsIsRunningFalse()
        {
            var monitor = new Mock<IProcessMonitor>();
            var automation = new Mock<IGameAutomationService>();

            var service = new MonitorControlService(monitor.Object, automation.Object);
            service.Start();
            Assert.True(service.IsRunning);

            monitor.Setup(m => m.Stop()).Throws(new InvalidOperationException("stop fail"));

            Assert.Throws<InvalidOperationException>(() => service.Stop());

            Assert.False(service.IsRunning);
            automation.Verify(a => a.Stop(), Times.AtLeastOnce);
        }

        [Fact]
        public void Stop_SuccessfulStop_ClearsIsRunning()
        {
            var monitor = new Mock<IProcessMonitor>();
            var automation = new Mock<IGameAutomationService>();

            var service = new MonitorControlService(monitor.Object, automation.Object);
            service.Start();
            Assert.True(service.IsRunning);

            service.Stop();
            Assert.False(service.IsRunning);
        }

        [Fact]
        public void Start_Stop_DoubleStop_IsIdempotent()
        {
            var monitor = new Mock<IProcessMonitor>();
            var automation = new Mock<IGameAutomationService>();

            var service = new MonitorControlService(monitor.Object, automation.Object);
            service.Start();
            service.Stop();
            service.Stop();

            Assert.False(service.IsRunning);
            automation.Verify(a => a.Stop(), Times.Once);
            monitor.Verify(m => m.Stop(), Times.Once);
        }

        [Fact]
        public void Start_DoubleStart_IsIdempotent()
        {
            var monitor = new Mock<IProcessMonitor>();
            var automation = new Mock<IGameAutomationService>();

            var service = new MonitorControlService(monitor.Object, automation.Object);
            service.Start();
            service.Start();

            Assert.True(service.IsRunning);
            automation.Verify(a => a.Start(), Times.Once);
            monitor.Verify(m => m.Start(), Times.Once);
        }
    }
}
