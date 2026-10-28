using GameHelper.Core.Models;

namespace GameHelper.Core.Abstractions;

/// <summary>Session-aware persistence; completion atomically removes the matching in-flight identity.</summary>
public interface IIdentifiedPlayTimeService : IPlayTimeService
{
    /// <summary>Fixes the observed event time and shares one contention budget across its writes.</summary>
    IDisposable BeginEvent(DateTime eventTime);

    /// <summary>Captured event time before lock/database waits; null outside an event.</summary>
    DateTime? EventTime { get; }

    /// <summary>Drains compatibility work; callers must hold no automation locks.</summary>
    void FlushPendingProjections();

    /// <summary>Starts or adopts an identity without changing it when the effective start shifts.</summary>
    void ResumeTracking(string dataKey, DateTime start, Guid sessionId, bool? launchedByHelper);

    /// <summary>Returns the original end time after an unsuccessful completion attempt.</summary>
    InFlightSession? GetPendingCompletion(string dataKey);

    /// <summary>Idempotently completes an interrupted session using its original identity.</summary>
    void WriteRecoveredSession(InFlightSession session, DateTime end);
}
