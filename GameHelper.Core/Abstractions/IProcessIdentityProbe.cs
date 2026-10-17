namespace GameHelper.Core.Abstractions;

/// <summary>
/// Answers whether a process id still belongs to the expected executable image.
/// Used when recovering in-flight sessions after a restart, so PID reuse never
/// resurrects a session for an unrelated executable.
/// </summary>
public interface IProcessIdentityProbe
{
    /// <summary>
    /// Tries to read the full image path of the live process with the given id.
    /// Returns false when the process is gone or inaccessible; <paramref name="imagePath"/>
    /// is null in that case.
    /// </summary>
    bool TryGetImagePath(int processId, out string? imagePath);
}