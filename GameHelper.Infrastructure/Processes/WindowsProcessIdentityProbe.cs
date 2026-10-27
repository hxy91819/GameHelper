using System;
using System.Diagnostics;
using GameHelper.Core.Abstractions;
using Microsoft.Extensions.Logging;

namespace GameHelper.Infrastructure.Processes;

/// <summary>
/// Resolves the current full image path of a live Windows process, used to verify that
/// a process id recovered from an in-flight session snapshot still belongs to the same
/// executable that was tracked before the restart.
/// </summary>
public sealed class WindowsProcessIdentityProbe : IProcessIdentityProbe
{
    private readonly ILogger<WindowsProcessIdentityProbe>? _logger;

    public WindowsProcessIdentityProbe(ILogger<WindowsProcessIdentityProbe>? logger = null)
    {
        _logger = logger;
    }

    /// <inheritdoc />
    public bool TryGetImagePath(int processId, out string? imagePath)
    {
        imagePath = null;
        try
        {
            using var process = Process.GetProcessById(processId);
            imagePath = WindowsProcessPathResolver.QueryFullProcessImageName(process.Handle);
            return !string.IsNullOrWhiteSpace(imagePath);
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "Process {ProcessId} is gone or inaccessible.", processId);
            return false;
        }
    }

    /// <inheritdoc />
    public bool TryGetStartTimeUtc(int processId, out DateTime startTimeUtc)
    {
        startTimeUtc = default;
        try
        {
            using var process = Process.GetProcessById(processId);
            startTimeUtc = process.StartTime.ToUniversalTime();
            return !process.HasExited;
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "Cannot read creation time of PID {ProcessId}.", processId);
            return false;
        }
    }
}
