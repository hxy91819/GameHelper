using GameHelper.Core.Models;

namespace GameHelper.Core.Abstractions;

/// <summary>Session-aware persistence; completion atomically removes the matching in-flight identity.</summary>
public interface IIdentifiedPlayTimeService : IPlayTimeService
{
    /// <summary>Starts or adopts an identity without changing it when the effective start shifts.</summary>
    void ResumeTracking(string dataKey, DateTime start, Guid sessionId, bool? launchedByHelper);

    /// <summary>Returns the original end time after an unsuccessful completion attempt.</summary>
    InFlightSession? GetPendingCompletion(string dataKey);

    /// <summary>Idempotently completes an interrupted session using its original identity.</summary>
    void WriteRecoveredSession(InFlightSession session, DateTime end);
}
