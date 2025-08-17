using System;

namespace GameHelper.Infrastructure.Processes
{
    // Simple abstraction to allow testing without depending directly on ManagementEventWatcher
    public interface IProcessEventWatcher
    {
        event Action<string>? ProcessEvent;
        void Start();
        void Stop();
    }
}
