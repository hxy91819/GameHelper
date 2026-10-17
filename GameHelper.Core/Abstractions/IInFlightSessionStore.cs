using GameHelper.Core.Models;

namespace GameHelper.Core.Abstractions;

/// <summary>
/// Persists the snapshot of in-flight play sessions so game sessions can survive
/// GameHelper restarts (deploy replace-restart, crash recovery). Implementations must
/// be crash-safe: a snapshot write must never leave a half-written file behind.
/// </summary>
public interface IInFlightSessionStore
{
    /// <summary>
    /// Reads the latest snapshot. Returns null when the file is absent or unreadable
    /// (nothing to recover); never throws for benign IO problems.
    /// </summary>
    InFlightSnapshot? Load();

    /// <summary>Saves the snapshot atomically (temporary file + rename).</summary>
    void Save(InFlightSnapshot snapshot);

    /// <summary>Removes the snapshot after it has been fully recovered.</summary>
    void Clear();
}