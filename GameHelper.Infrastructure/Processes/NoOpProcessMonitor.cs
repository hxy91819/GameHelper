using System;
using GameHelper.Core.Abstractions;

namespace GameHelper.Infrastructure.Processes
{
    public class NoOpProcessMonitor : IProcessMonitor
    {
        public event Action<string>? ProcessStarted;
        public event Action<string>? ProcessStopped;

        public void Start()
        {
            // no-op
        }

        public void Stop()
        {
            // no-op
        }

        // Helpers to simulate events in future tests if needed
        public void SimulateStart(string processName) => ProcessStarted?.Invoke(processName);
        public void SimulateStop(string processName) => ProcessStopped?.Invoke(processName);
    }
}
