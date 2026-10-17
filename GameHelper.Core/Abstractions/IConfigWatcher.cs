namespace GameHelper.Core.Abstractions;

/// <summary>
/// Watches the live configuration file and invokes a callback when it changes on disk,
/// so externally edited configuration is picked up without restarting GameHelper.
/// </summary>
public interface IConfigWatcher : IDisposable
{
    /// <summary>
    /// Starts watching the configuration file.
    /// Implementations are idempotent and best-effort: starting when the file does not
    /// exist yet is fine, and watcher creation failures must degrade to a no-op instead
    /// of throwing into service startup paths.
    /// </summary>
    void Start();
}