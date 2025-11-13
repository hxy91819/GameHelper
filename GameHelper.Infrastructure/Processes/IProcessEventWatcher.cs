using System;
using GameHelper.Core.Models;

namespace GameHelper.Infrastructure.Processes
{
    // Simple abstraction to allow testing without depending directly on ManagementEventWatcher
    public interface IProcessEventWatcher
    {
        event Action<ProcessEventInfo>? ProcessEvent;
        void Start();
        void Stop();
    }
}
