using System;

namespace GameHelper.Core.Abstractions
{
    public interface IProcessMonitor
    {
        event Action<string>? ProcessStarted;
        event Action<string>? ProcessStopped;
        void Start();
        void Stop();
    }
}
