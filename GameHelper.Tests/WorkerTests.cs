using System;
using System.Threading;
using System.Threading.Tasks;
using GameHelper.ConsoleHost;
using GameHelper.Core.Abstractions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace GameHelper.Tests
{
    // Simple fake monitor to observe Start/Stop calls
    file sealed class FakeMonitor : IProcessMonitor
    {
        public int StartCalls { get; private set; }
        public int StopCalls { get; private set; }
        public event Action<string>? ProcessStarted;
        public event Action<string>? ProcessStopped;
        public void Start() => StartCalls++;
        public void Stop() => StopCalls++;
        public void Dispose() { }
        public void RaiseStart(string name) => ProcessStarted?.Invoke(name);
        public void RaiseStop(string name) => ProcessStopped?.Invoke(name);
    }

    file sealed class FakeHdr : IHdrController
    {
        public bool IsEnabled { get; private set; }
        public void Enable() => IsEnabled = true;
        public void Disable() => IsEnabled = false;
    }

    file sealed class FakeAutomation : IGameAutomationService
    {
        public int StartCalls { get; private set; }
        public int StopCalls { get; private set; }
        public void Start() => StartCalls++;
        public void Stop() => StopCalls++;
    }

    public class WorkerTests
    {
        [Fact]
        public async Task Execute_StartsAndStops_Monitor()
        {
            var monitor = new FakeMonitor();
            var hdr = new FakeHdr();
            var automation = new FakeAutomation();
            var logger = NullLogger<Worker>.Instance;
            var worker = new Worker(logger, monitor, hdr, automation);

            using var cts = new CancellationTokenSource();

            // Start the worker
            var runTask = worker.StartAsync(cts.Token);
            await Task.Delay(50);
            Assert.Equal(1, monitor.StartCalls);
            Assert.Equal(1, automation.StartCalls);

            // Stop the worker
            await worker.StopAsync(CancellationToken.None);
            Assert.Equal(1, monitor.StopCalls);
            Assert.Equal(1, automation.StopCalls);

            // Cancel background delay to finish StartAsync if needed
            cts.Cancel();
            try { await runTask; } catch (OperationCanceledException) { /* expected */ }
        }
    }
}
