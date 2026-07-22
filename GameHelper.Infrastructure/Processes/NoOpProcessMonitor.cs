using System;
using System.Threading;
using GameHelper.Core.Abstractions;
using GameHelper.Core.Models;

namespace GameHelper.Infrastructure.Processes
{
    public class NoOpProcessMonitor : IProcessMonitor
    {
        private ProcessObservationPolicy _policy = ProcessObservationPolicy.ObserveAll();

        public event Action<ProcessEventInfo>? ProcessStarted;
        public event Action<ProcessEventInfo>? ProcessStopped;

        public void Configure(ProcessObservationPolicy policy)
        {
            ArgumentNullException.ThrowIfNull(policy);
            Volatile.Write(ref _policy, policy);
        }

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
        public void SimulateStart(ProcessEventInfo processInfo)
        {
            if (Volatile.Read(ref _policy).Includes(processInfo.ExecutableName))
            {
                ProcessStarted?.Invoke(processInfo);
            }
        }

        public void SimulateStop(ProcessEventInfo processInfo)
        {
            var policy = Volatile.Read(ref _policy);
            if (policy.ObserveStopEvents && policy.Includes(processInfo.ExecutableName))
            {
                ProcessStopped?.Invoke(processInfo);
            }
        }
    }
}
