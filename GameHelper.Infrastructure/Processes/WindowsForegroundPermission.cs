using System.Runtime.InteropServices;

namespace GameHelper.Infrastructure.Processes;

/// <summary>Best-effort Windows foreground hand-off for processes started by GameHelper.</summary>
public static class WindowsForegroundPermission
{
    private const int AsfwAny = -1;

    /// <summary>
    /// Allows all processes, including the background host and the game it starts, to take the
    /// foreground. Windows may refuse when the caller has no foreground rights; launching must
    /// not depend on the result.
    /// </summary>
    public static bool AllowAnyProcess() =>
        OperatingSystem.IsWindows() && AllowSetForegroundWindow(AsfwAny);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AllowSetForegroundWindow(int processId);
}
