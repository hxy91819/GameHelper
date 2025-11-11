using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using GameHelper.ConsoleHost;
using GameHelper.Core.Abstractions;
using GameHelper.Core.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace GameHelper.Tests
{
    // Simple fake monitor to observe Start/Stop calls
    file sealed class FakeMonitor : IProcessMonitor
    {
        public int StartCalls { get; private set; }
        public int StopCalls { get; private set; }
        public event Action<ProcessEventInfo>? ProcessStarted;
        public event Action<ProcessEventInfo>? ProcessStopped;
        public void Start() => StartCalls++;
        public void Stop() => StopCalls++;
        public void Dispose() { }
        public void RaiseStart(ProcessEventInfo info) => ProcessStarted?.Invoke(info);
        public void RaiseStop(ProcessEventInfo info) => ProcessStopped?.Invoke(info);
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

    file sealed class FakeConfigProvider : IConfigProvider
    {
        private readonly Dictionary<string, GameConfig> _configs = new();

        public IReadOnlyDictionary<string, GameConfig> Load() => _configs;
        public void Save(IReadOnlyDictionary<string, GameConfig> configs)
        {
            _configs.Clear();
            foreach (var kv in configs)
            {
                _configs[kv.Key] = kv.Value;
            }
        }
    }

    public class WorkerTests
    {
        [Fact]
        public async Task Execute_StartsAndStops_Monitor()
        {
            var monitor = new FakeMonitor();
            var hdr = new FakeHdr();
            var automation = new FakeAutomation();
            var configProvider = new FakeConfigProvider();
            var logger = NullLogger<Worker>.Instance;
            var worker = new Worker(logger, monitor, hdr, automation, configProvider);

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
