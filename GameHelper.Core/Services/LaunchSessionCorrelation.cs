namespace GameHelper.Core.Services;

/// <summary>Correlates one live request with the first fresh session in its bounded observation window.
/// The automation service holds its state lock around all calls.</summary>
internal sealed class LaunchSessionCorrelation(TimeProvider clock)
{
    private sealed record Pending(Guid RequestId, int? ExpectedPid, DateTimeOffset Deadline);
    private readonly Dictionary<string, Pending> pending = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, (Guid RequestId, int ProcessId)> active = new(StringComparer.OrdinalIgnoreCase);
    internal IReadOnlyDictionary<string, (Guid RequestId, int ProcessId)> Snapshot() => new Dictionary<string, (Guid, int)>(active, StringComparer.OrdinalIgnoreCase);
    internal bool Begin(string key, Guid requestId, int? expectedPid, DateTimeOffset deadline, bool alreadyRunning)
    {
        if (alreadyRunning || deadline <= clock.GetUtcNow()) return false;
        pending[key] = new(requestId, expectedPid, deadline);
        return true;
    }
    internal void Observe(string key, int? pid, bool firstForKey, bool preexisting)
    {
        if (!pending.Remove(key, out var request)) return;
        if (!firstForKey || preexisting || pid is null || clock.GetUtcNow() >= request.Deadline) return;
        // Opaque URI/shortcut launches deliberately accept the first fresh matching session.
        // A known launcher PID retains strict matching; it never falls back to another process.
        if (request.ExpectedPid is { } expected && expected != pid) return;
        active[key] = (request.RequestId, pid.Value);
    }
    internal void Cancel(string key) => pending.Remove(key);
    internal void Release(string key) => active.Remove(key);
    internal void ClearPending() => pending.Clear();
    internal void ClearActive() => active.Clear();
}
