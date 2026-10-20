using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

// A GUI-subsystem entry point creates no console of its own. Create the console minimized
// without activation, then show it at normal size; ConsoleHost alone starts too late to do this.
var executable = Path.Combine(AppContext.BaseDirectory, "GameHelper.ConsoleHost.exe");
if (!File.Exists(executable)) return 1;
var readyName = @"Local\GameHelper.LaunchReady." + Guid.NewGuid().ToString("N");
using var ready = new EventWaitHandle(false, EventResetMode.ManualReset, readyName);
Environment.SetEnvironmentVariable("GAMEHELPER_LAUNCH_READY_EVENT", readyName);
var command = new StringBuilder(Quote(executable)).Append(" --in-windows-terminal");
foreach (var argument in args) command.Append(' ').Append(Quote(argument));
var startup = new Native.StartupInfo { Size = Marshal.SizeOf<Native.StartupInfo>(), Flags = 1, ShowWindow = 7 };
// Do not inherit redirected standard handles from a developer's shell or the GUI launcher.
if (!Native.CreateProcess(executable, command, IntPtr.Zero, IntPtr.Zero, false, 0x10,
        IntPtr.Zero, AppContext.BaseDirectory, ref startup, out var created)) return 1;
Native.CloseHandle(created.Thread);
Native.CloseHandle(created.Process);
Process child;
try { child = Process.GetProcessById(created.ProcessId); }
catch (ArgumentException) { return 0; } // A one-shot command can already have finished.
using var childLifetime = child;
var deadline = Stopwatch.StartNew();
// ConsoleHost signals after its first console output, once its console connection is initialized.
while (!child.HasExited && !ready.WaitOne(50) && deadline.Elapsed < TimeSpan.FromSeconds(10)) { }
while (!child.HasExited && deadline.Elapsed < TimeSpan.FromSeconds(10))
{
    var window = Native.FindConsoleWindow(child.Id);
    if (window != IntPtr.Zero)
    {
        Native.ShowWindow(window, 4); // SW_SHOWNOACTIVATE
        if (Native.IsWindowVisible(window)) return 0;
        break;
    }
    Thread.Sleep(50);
}
if (child.HasExited) return child.ExitCode;
child.Kill(); // A failed launcher must not leave an invisible single-instance lock holder.
return 1;

static string Quote(string value)
{
    var result = new StringBuilder("\"");
    var slashes = 0;
    foreach (var character in value)
    {
        if (character == '\\') { slashes++; continue; }
        result.Append('\\', character == '"' ? slashes * 2 + 1 : slashes).Append(character);
        slashes = 0;
    }
    return result.Append('\\', slashes * 2).Append('"').ToString();
}

internal static class Native
{
    internal static IntPtr FindConsoleWindow(int processId)
    {
        var found = IntPtr.Zero;
        EnumWindows((window, _) =>
        {
            GetWindowThreadProcessId(window, out var owner);
            var className = new StringBuilder(64);
            if (owner == processId && GetClassName(window, className, className.Capacity) > 0
                && className.ToString() == "ConsoleWindowClass")
            {
                found = window;
                return false;
            }
            return true;
        }, IntPtr.Zero);
        return found;
    }
    private delegate bool WindowCallback(IntPtr window, IntPtr state);
    [DllImport("user32.dll")] private static extern bool EnumWindows(WindowCallback callback, IntPtr state);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out int processId);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr window, StringBuilder name, int length);
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct StartupInfo
    {
        public int Size;
        public string? Reserved, Desktop, Title;
        public int X, Y, Width, Height, XChars, YChars, FillAttribute, Flags;
        public short ShowWindow, ReservedSize;
        public IntPtr ReservedBytes, Input, Output, Error;
    }
    [StructLayout(LayoutKind.Sequential)]
    internal struct ProcessInformation { public IntPtr Process, Thread; public int ProcessId, ThreadId; }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern bool CreateProcess(string application, StringBuilder command, IntPtr processAttributes,
        IntPtr threadAttributes, bool inheritHandles, uint flags, IntPtr environment, string directory,
        ref StartupInfo startup, out ProcessInformation process);
    [DllImport("kernel32.dll")] internal static extern bool CloseHandle(IntPtr handle);
    [DllImport("user32.dll")] internal static extern bool ShowWindow(IntPtr window, int command);
    [DllImport("user32.dll")] internal static extern bool IsWindowVisible(IntPtr window);
}
