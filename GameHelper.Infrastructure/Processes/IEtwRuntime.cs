using GameHelper.Core.Models;
using GameHelper.Core.Abstractions;

namespace GameHelper.Infrastructure.Processes;

// OS boundary: lifecycle tests exercise the real monitor using in-memory sessions.
internal interface IEtwRuntime
{
    void EnsureElevated();
    IEtwSession CreateSession(string name);
    IReadOnlyCollection<string> GetActiveSessionNames();
    void StopSession(string name);
    ProcessProbe InspectProcess(int processId);
    IReadOnlyList<EtwProcessEvent> CaptureExistingProcesses(ProcessObservationPolicy policy) => Array.Empty<EtwProcessEvent>();
    Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken);
}

internal interface IEtwSession : IDisposable
{
    bool IsAlive { get; }
    void Start(Action<EtwProcessEvent> started, Action<EtwProcessEvent> stopped, Action ended);
}

internal readonly record struct EtwProcessEvent(ProcessEventInfo Info, DateTime TimestampUtc);

// Missing is distinct from access denied: an unknown identity must not fabricate an exit.
internal readonly record struct ProcessProbe(bool HasExited, DateTime? StartTimeUtc);
