using System;
using System.Threading;
using System.Threading.Tasks;
using GameHelper.ConsoleHost;
using GameHelper.Core.Abstractions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Moq;

namespace GameHelper.Tests
{
    file sealed class FakeMonitorControlService : IMonitorControlService
    {
        public int StartCalls { get; private set; }
        public int StopCalls { get; private set; }
        public bool IsRunning { get; private set; }

        public void Start() => StartCalls++;
        public void Stop() => StopCalls++;
    }

    public class WorkerTests
    {
        [Fact]
        public async Task ShutdownStopsTrayBeforeMonitor()
        {
            var order = new List<string>();
            var monitor = new Mock<IMonitorControlService>();
            monitor.Setup(service => service.Start()).Callback(() => order.Add("monitor start"));
            monitor.Setup(service => service.Stop()).Callback(() => order.Add("monitor stop"));
            var tray = new Mock<ISystemTrayService>();
            tray.Setup(service => service.Start()).Callback(() => order.Add("tray start"));
            tray.Setup(service => service.Stop()).Callback(() => order.Add("tray stop"));
            using var worker = new Worker(NullLogger<Worker>.Instance, monitor.Object, tray.Object);
            await worker.StartAsync(CancellationToken.None);
            await worker.StopAsync(CancellationToken.None);
            Assert.Equal(new[] { "monitor start", "tray start", "tray stop", "monitor stop" }, order);
        }

        [Fact]
        public async Task TrayStartupFailureRollsBackBothServices()
        {
            var monitor = new Mock<IMonitorControlService>();
            var tray = new Mock<ISystemTrayService>();
            tray.Setup(service => service.Start()).Throws(new InvalidOperationException("tray failed"));
            using var worker = new Worker(NullLogger<Worker>.Instance, monitor.Object, tray.Object);
            await Assert.ThrowsAsync<InvalidOperationException>(() => worker.StartAsync(CancellationToken.None));
            tray.Verify(service => service.Stop(), Times.Once);
            monitor.Verify(service => service.Stop(), Times.Once);
        }

        [Fact]
        public async Task TrayCleanupFailureStillStopsMonitor()
        {
            var monitor = new Mock<IMonitorControlService>();
            var tray = new Mock<ISystemTrayService>();
            tray.Setup(service => service.Stop()).Throws(new InvalidOperationException("cleanup failed"));
            using var worker = new Worker(NullLogger<Worker>.Instance, monitor.Object, tray.Object);
            await worker.StartAsync(CancellationToken.None);
            await worker.StopAsync(CancellationToken.None);
            monitor.Verify(service => service.Stop(), Times.Once);
        }

        [Fact]
        public async Task Execute_StartsAndStops_Monitor()
        {
            var monitorControlService = new FakeMonitorControlService();
            var logger = NullLogger<Worker>.Instance;
            var worker = new Worker(logger, monitorControlService);

            using var cts = new CancellationTokenSource();

            // Start the worker
            var runTask = worker.StartAsync(cts.Token);
            await Task.Delay(50);
            Assert.Equal(1, monitorControlService.StartCalls);

            // Stop the worker
            await worker.StopAsync(CancellationToken.None);
            Assert.Equal(1, monitorControlService.StopCalls);

            // Cancel background delay to finish StartAsync if needed
            cts.Cancel();
            try { await runTask; } catch (OperationCanceledException) { /* expected */ }
        }
    }
}
