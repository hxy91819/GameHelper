using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
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
    private const uint MaxImagePathLength = 1024;

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
            var size = MaxImagePathLength;
            var builder = new StringBuilder((int)size);
            if (NativeMethods.QueryFullProcessImageNameNative(process.Handle, 0, builder, ref size))
            {
                imagePath = builder.ToString();
                return !string.IsNullOrWhiteSpace(imagePath);
            }

            return false;
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "Process {ProcessId} is gone or inaccessible.", processId);
            return false;
        }
    }

    private static class NativeMethods
    {
        [DllImport("kernel32.dll", EntryPoint = "QueryFullProcessImageNameW", SetLastError = true, CharSet = CharSet.Unicode)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool QueryFullProcessImageNameNative(
            IntPtr hProcess,
            uint dwFlags,
            StringBuilder lpExeName,
            ref uint lpdwSize);
    }
}