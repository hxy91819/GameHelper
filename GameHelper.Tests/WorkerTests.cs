using System;
using System.Threading;
using System.Threading.Tasks;
using GameHelper.ConsoleHost;
using GameHelper.Core.Abstractions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

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
