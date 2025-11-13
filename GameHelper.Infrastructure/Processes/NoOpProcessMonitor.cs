using System;
using GameHelper.Core.Abstractions;
using GameHelper.Core.Models;

namespace GameHelper.Infrastructure.Processes
{
    public class NoOpProcessMonitor : IProcessMonitor
    {
        public event Action<ProcessEventInfo>? ProcessStarted;
        public event Action<ProcessEventInfo>? ProcessStopped;

        public void Start()
        {
            // no-op
        }

        public void Stop()
        {
            // no-op
        }

        public void Dispose()
        {
            // no-op
        }

        // Helpers to simulate events in future tests if needed
        public void SimulateStart(ProcessEventInfo processInfo) => ProcessStarted?.Invoke(processInfo);
        public void SimulateStop(ProcessEventInfo processInfo) => ProcessStopped?.Invoke(processInfo);
    }
}
