# Read-only console visibility probe. Run in a separate helper process so attaching
# to the target console never detaches the verifier or the user's terminal.
param(
    [Parameter(Mandatory = $true)]
    [ValidateRange(1, 2147483647)]
    [int] $TargetProcessId
)

$ErrorActionPreference = 'Stop'
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class GameHelperConsoleWindowProbe {
    [DllImport("kernel32.dll", SetLastError = true)] public static extern bool AttachConsole(uint processId);
    [DllImport("kernel32.dll")] public static extern bool FreeConsole();
    [DllImport("kernel32.dll")] public static extern IntPtr GetConsoleWindow();
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32.dll")] public static extern IntPtr GetAncestor(IntPtr window, uint flags);
}
'@

[GameHelperConsoleWindowProbe]::FreeConsole() | Out-Null
if (-not [GameHelperConsoleWindowProbe]::AttachConsole($TargetProcessId)) {
    throw "Cannot inspect console for PID ${TargetProcessId}: Win32 error $([Runtime.InteropServices.Marshal]::GetLastWin32Error())."
}
try {
    $window = [GameHelperConsoleWindowProbe]::GetConsoleWindow()
    # Windows Terminal owns ConPTY's hidden compatibility HWND. Inspect its root
    # owner (GA_ROOTOWNER=3), which is the displayed terminal, rather than rejecting
    # a healthy terminal because its compatibility window is not displayed.
    # https://github.com/microsoft/terminal/blob/main/src/cascadia/TerminalApp/TerminalPage.cpp
    $displayWindow = [GameHelperConsoleWindowProbe]::GetAncestor($window, 3)
    if ($displayWindow -eq [IntPtr]::Zero) { $displayWindow = $window }
    $visible = $displayWindow -ne [IntPtr]::Zero -and [GameHelperConsoleWindowProbe]::IsWindowVisible($displayWindow)
    [pscustomobject]@{ ProcessId = $TargetProcessId; WindowHandle = $window.ToInt64(); DisplayWindowHandle = $displayWindow.ToInt64(); Visible = $visible } | ConvertTo-Json -Compress
    if (-not $visible) { exit 1 }
}
finally {
    [GameHelperConsoleWindowProbe]::FreeConsole() | Out-Null
}
