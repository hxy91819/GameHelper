using System.Diagnostics;
using System.ComponentModel;
using System.Runtime.InteropServices;
namespace GameHelper.Desktop;

internal static class ConsolePower
{
    internal static void Execute(ConsoleMenuCommand command)
    {
        if (command == ConsoleMenuCommand.Sleep)
        {
            if (!SetSuspendState(false, false, false)) throw new Win32Exception(Marshal.GetLastWin32Error());
            return;
        }
        var start = new ProcessStartInfo(System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "shutdown.exe")) { UseShellExecute = false, CreateNoWindow = true };
        start.ArgumentList.Add(command == ConsoleMenuCommand.Restart ? "/r" : "/s");
        start.ArgumentList.Add("/t"); start.ArgumentList.Add("0");
        using var process = Process.Start(start) ?? throw new InvalidOperationException("无法请求 Windows 电源操作。");
        process.WaitForExit();
        if (process.ExitCode != 0) throw new InvalidOperationException($"Windows 电源请求失败：{process.ExitCode}");
    }
    [DllImport("powrprof.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.U1)]
    private static extern bool SetSuspendState([MarshalAs(UnmanagedType.U1)] bool hibernate, [MarshalAs(UnmanagedType.U1)] bool force, [MarshalAs(UnmanagedType.U1)] bool disableWake);
}
