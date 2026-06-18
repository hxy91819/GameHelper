using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using GameHelper.Core.Abstractions;
using Microsoft.Extensions.Logging;

namespace GameHelper.Infrastructure.Processes;

/// <summary>
/// Resolves live Windows process executable paths on demand.
/// </summary>
public sealed class WindowsProcessPathResolver : IProcessPathResolver
{
    private readonly ILogger<WindowsProcessPathResolver>? _logger;

    public WindowsProcessPathResolver(ILogger<WindowsProcessPathResolver>? logger = null)
    {
        _logger = logger;
    }

    public string? TryResolveExecutablePath(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return QueryFullProcessImageName(process.Handle);
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "Unable to resolve executable path for process {ProcessId}", processId);
            return null;
        }
    }

    private static string? QueryFullProcessImageName(IntPtr processHandle)
    {
        const uint maxPath = 1024;
        uint size = maxPath;
        var sb = new StringBuilder((int)size);

        return NativeMethods.QueryFullProcessImageNameNative(processHandle, 0, sb, ref size)
            ? sb.ToString()
            : null;
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
