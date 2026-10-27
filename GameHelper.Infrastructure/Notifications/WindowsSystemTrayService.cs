using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using GameHelper.Core.Abstractions;
using Microsoft.Extensions.Logging;

namespace GameHelper.Infrastructure.Notifications;

internal enum SystemTrayAction : uint { OpenLibrary = 1, Snooze, ResetCycle, Exit }

/// <summary>Formatting and command routing independent of the Windows message pump.</summary>
public static class SystemTrayLogic
{
    public static bool ShouldStart(bool background, string? dataDirectory) =>
        background && string.IsNullOrWhiteSpace(dataDirectory);

    public static string Summary(bool monitoring, IReadOnlyDictionary<string, DateTime?> sessions,
        IReadOnlyDictionary<string, string> names, DateTime now)
    {
        var state = monitoring ? "监控中" : "监控已停止";
        var games = sessions.OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair =>
        {
            var minutes = pair.Value is { } start ? Math.Max(0, (long)(now - start).TotalMinutes) : (long?)null;
            return $"{names.GetValueOrDefault(pair.Key, pair.Key)}（{(minutes is null ? "时长未知" : $"本局 {minutes} 分钟")}）";
        });
        return $"GameHelper · {state}\n{(sessions.Count == 0 ? "暂无运行中的游戏" : string.Join("；", games))}";
    }

    public static string Tooltip(string summary)
    {
        var text = summary.Replace('\n', ' ');
        if (text.Length <= 127) return text;
        var length = char.IsHighSurrogate(text[125]) ? 125 : 126;
        return text[..length] + "…";
    }

    internal static bool Dispatch(uint command, IBreakReminderService reminders, Action openLibrary, Action exit)
    {
        switch ((SystemTrayAction)command)
        {
            case SystemTrayAction.OpenLibrary: openLibrary(); break;
            case SystemTrayAction.Snooze: reminders.Snooze(TimeSpan.FromMinutes(15)); break;
            case SystemTrayAction.ResetCycle: reminders.ResetCycle(); break;
            case SystemTrayAction.Exit: exit(); break;
            default: return false;
        }
        return true;
    }

    /// <summary>Start the WPF library beside the host; a null config lets the library use its default path.</summary>
    public static ProcessStartInfo LibraryStartInfo(string baseDirectory, string? config, Func<string, bool> exists)
    {
        var executable = Path.Combine(baseDirectory, "GameHelper.Desktop.exe");
        if (!exists(executable)) executable = Path.Combine(baseDirectory, "desktop", "GameHelper.Desktop.exe");
        var start = new ProcessStartInfo(executable) { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(executable)! };
        if (config is null) return start;
        start.ArgumentList.Add("--config");
        start.ArgumentList.Add(config);
        return start;
    }
}

/// <summary>Persistent native tray entry owned by a dedicated STA thread; Desktop remains an on-demand UI.</summary>
public sealed class WindowsSystemTrayService : ISystemTrayService
{
    private const uint CallbackMessage = 0x8001;
    private readonly object _gate = new();
    private readonly Func<string> _summary;
    private readonly Action _openLibrary;
    private readonly Action _exit;
    private readonly IBreakReminderService _reminders;
    private readonly ILogger<WindowsSystemTrayService> _logger;
    private readonly NativeMethods.WindowProcedure _procedure;
    private readonly ManualResetEventSlim _ready = new(false);
    private readonly string _className = "GameHelper.SystemTray." + Guid.NewGuid().ToString("N");
    private Thread? _thread;
    private IntPtr _window;
    private Exception? _startupError;
    private volatile bool _stopRequested;
    private bool _disposed;
    // Native resources below are owned exclusively by the STA thread.
    private uint _taskbarCreated;
    private bool _hasIcon;

    public WindowsSystemTrayService(Func<string> summary, Action openLibrary, Action exit,
        IBreakReminderService reminders, ILogger<WindowsSystemTrayService> logger)
    {
        _summary = summary;
        _openLibrary = openLibrary;
        _exit = exit;
        _reminders = reminders;
        _logger = logger;
        _procedure = HandleMessage;
    }

    public bool IsRunning => !_stopRequested && Volatile.Read(ref _window) != IntPtr.Zero;

    public void Start()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (IsRunning) return;
            if (_thread?.IsAlive == true) throw new InvalidOperationException("Previous tray thread is still stopping.");
            _ready.Reset();
            _startupError = null;
            _stopRequested = false;
            _thread = new Thread(RunWindow) { IsBackground = true, Name = "GameHelper system tray" };
            _thread.SetApartmentState(ApartmentState.STA);
            _thread.Start();
            try
            {
                if (!_ready.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException("Tray startup timed out.");
                if (_startupError is not null) throw new InvalidOperationException("Tray startup failed.", _startupError);
                if (!IsRunning) throw new InvalidOperationException("Tray window is unavailable.");
            }
            catch { Stop(); throw; }
        }
    }

    public void Stop()
    {
        lock (_gate)
        {
            _stopRequested = true;
            SafeCleanup(() =>
            {
                var window = Volatile.Read(ref _window);
                if (window != IntPtr.Zero) NativeMethods.PostMessage(window, 0x0010, 0, IntPtr.Zero);
            });
            SafeCleanup(() =>
            {
                if (_thread is not null && _thread != Thread.CurrentThread && !_thread.Join(TimeSpan.FromSeconds(5)))
                    _logger.LogWarning("Tray thread did not exit within five seconds");
            });
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            Stop();
            _disposed = true;
            if (_thread?.IsAlive != true) _ready.Dispose();
        }
    }

    private void RunWindow()
    {
        var instance = NativeMethods.GetModuleHandle(null);
        ushort windowClass = 0;
        IntPtr window = IntPtr.Zero;
        try
        {
            _taskbarCreated = NativeMethods.RegisterWindowMessage("TaskbarCreated");
            if (_taskbarCreated == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
            var registration = new NativeMethods.WindowClass
            {
                Size = (uint)Marshal.SizeOf<NativeMethods.WindowClass>(), Procedure = _procedure,
                Instance = instance, ClassName = _className
            };
            windowClass = NativeMethods.RegisterClassEx(ref registration);
            if (windowClass == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
            // A hidden top-level window receives Explorer's TaskbarCreated broadcast.
            window = NativeMethods.CreateWindowEx(0, _className, "GameHelper", 0,
                0, 0, 0, 0, IntPtr.Zero, IntPtr.Zero, instance, IntPtr.Zero);
            if (window == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
            // Elevated monitoring still needs Explorer's lower-integrity restart broadcast.
            if (!NativeMethods.ChangeWindowMessageFilterEx(window, _taskbarCreated, 1, IntPtr.Zero))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            UpdateIcon(window);
            if (NativeMethods.SetTimer(window, 1, 5000, IntPtr.Zero) == 0)
                throw new Win32Exception(Marshal.GetLastWin32Error());
            Volatile.Write(ref _window, window);
            _ready.Set();
            if (_stopRequested) return;
            int result;
            while ((result = NativeMethods.GetMessage(out var message, IntPtr.Zero, 0, 0)) > 0)
            {
                NativeMethods.TranslateMessage(ref message);
                NativeMethods.DispatchMessage(ref message);
            }
            if (result < 0) throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        catch (Exception ex)
        {
            _startupError = ex;
            _logger.LogWarning(ex, "System tray failed");
        }
        finally
        {
            _stopRequested = true;
            SafeCleanup(() => { if (window != IntPtr.Zero) NativeMethods.KillTimer(window, 1); });
            SafeCleanup(() => RemoveIcon(window));
            SafeCleanup(() => { if (window != IntPtr.Zero) NativeMethods.DestroyWindow(window); });
            Volatile.Write(ref _window, IntPtr.Zero);
            SafeCleanup(() => { if (windowClass != 0) NativeMethods.UnregisterClass(_className, instance); });
            _ready.Set();
        }
    }

    private IntPtr HandleMessage(IntPtr window, uint message, nuint wParam, IntPtr lParam)
    {
        try
        {
            if (_taskbarCreated != 0 && message == _taskbarCreated)
            {
                _hasIcon = false;
                UpdateIcon(window);
                return IntPtr.Zero;
            }
            switch (message)
            {
                case 0x0113: UpdateIcon(window); return IntPtr.Zero; // WM_TIMER
                case CallbackMessage:
                    var eventId = unchecked((uint)lParam.ToInt64()) & 0xffff;
                    if (eventId == 0x0203) Dispatch((uint)SystemTrayAction.OpenLibrary); // WM_LBUTTONDBLCLK
                    else if (eventId is 0x0205 or 0x007b) ShowMenu(window); // WM_RBUTTONUP / WM_CONTEXTMENU
                    return IntPtr.Zero;
                case 0x0010: NativeMethods.DestroyWindow(window); return IntPtr.Zero;
                case 0x0002: NativeMethods.PostQuitMessage(0); return IntPtr.Zero;
            }
        }
        catch (Exception ex) { _logger.LogWarning(ex, "Tray interaction failed"); return IntPtr.Zero; }
        return NativeMethods.DefWindowProc(window, message, wParam, lParam);
    }

    private void Dispatch(uint command) => SystemTrayLogic.Dispatch(command, _reminders, _openLibrary, _exit);

    private void ShowMenu(IntPtr window)
    {
        var menu = NativeMethods.CreatePopupMenu();
        if (menu == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
        try
        {
            // Each summary row is disabled; escape ampersands so game names remain literal.
            foreach (var line in _summary().Split('\n'))
                Append(menu, 0x0001, 0, line.Replace("&", "&&"));
            Append(menu, 0x0800, 0, null);
            Append(menu, 0, (uint)SystemTrayAction.OpenLibrary, "打开 GameHelper 游戏库");
            Append(menu, _reminders.IsRunning ? 0u : 1u, (uint)SystemTrayAction.Snooze, "推迟休息提醒 15 分钟");
            Append(menu, _reminders.IsRunning ? 0u : 1u, (uint)SystemTrayAction.ResetCycle, "重置本轮休息计时");
            Append(menu, 0x0800, 0, null);
            Append(menu, 0, (uint)SystemTrayAction.Exit, "退出 GameHelper 后台监控");
            if (!NativeMethods.GetCursorPos(out var point)) throw new Win32Exception(Marshal.GetLastWin32Error());
            NativeMethods.SetForegroundWindow(window);
            var command = NativeMethods.TrackPopupMenuEx(menu, 0x0100 | 0x0002, point.X, point.Y, window, IntPtr.Zero);
            NativeMethods.PostMessage(window, 0, 0, IntPtr.Zero); // Required for notification-area menu dismissal.
            Dispatch(command);
        }
        finally { NativeMethods.DestroyMenu(menu); }
    }

    private static void Append(IntPtr menu, uint flags, uint command, string? text)
    {
        if (!NativeMethods.AppendMenu(menu, flags, command, text)) throw new Win32Exception(Marshal.GetLastWin32Error());
    }

    private NativeMethods.IconData IconData(IntPtr window) => new()
    {
        Size = (uint)Marshal.SizeOf<NativeMethods.IconData>(), Window = window, Id = 1,
        Flags = 1 | 2 | 4, Callback = CallbackMessage,
        Icon = NativeMethods.LoadIcon(IntPtr.Zero, new IntPtr(32512)),
        Tip = SystemTrayLogic.Tooltip(_summary()), Text = string.Empty, Title = string.Empty
    };

    private void UpdateIcon(IntPtr window)
    {
        var data = IconData(window);
        if (!NativeMethods.ShellNotifyIcon(_hasIcon ? 1u : 0u, ref data))
        {
            _hasIcon = false;
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        _hasIcon = true;
        // Keep legacy callback semantics for native double-click delivery.
    }

    private void RemoveIcon(IntPtr window)
    {
        if (!_hasIcon) return;
        var data = new NativeMethods.IconData { Size = (uint)Marshal.SizeOf<NativeMethods.IconData>(), Window = window, Id = 1 };
        NativeMethods.ShellNotifyIcon(2, ref data);
        _hasIcon = false;
    }

    private void SafeCleanup(Action cleanup)
    {
        try { cleanup(); }
        catch (Exception ex) { _logger.LogWarning(ex, "Tray cleanup failed"); }
    }

    private static class NativeMethods
    {
        [StructLayout(LayoutKind.Sequential)]
        public struct Point { public int X, Y; }
        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)] public static extern bool ChangeWindowMessageFilterEx(IntPtr window, uint message, uint action, IntPtr changeFilter);
        [DllImport("user32.dll", EntryPoint = "RegisterWindowMessageW", CharSet = CharSet.Unicode)]
        public static extern uint RegisterWindowMessage(string name);
        [DllImport("user32.dll", EntryPoint = "PostMessageW")]
        [return: MarshalAs(UnmanagedType.Bool)] public static extern bool PostMessage(IntPtr window, uint message, nuint wParam, IntPtr lParam);
        [DllImport("user32.dll")] public static extern IntPtr CreatePopupMenu();
        [DllImport("user32.dll", EntryPoint = "AppendMenuW", CharSet = CharSet.Unicode)]
        [return: MarshalAs(UnmanagedType.Bool)] public static extern bool AppendMenu(IntPtr menu, uint flags, nuint id, string? text);
        [DllImport("user32.dll")] public static extern uint TrackPopupMenuEx(IntPtr menu, uint flags, int x, int y, IntPtr window, IntPtr parameters);
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static extern bool DestroyMenu(IntPtr menu);
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static extern bool GetCursorPos(out Point point);
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static extern bool SetForegroundWindow(IntPtr window);
        public delegate IntPtr WindowProcedure(IntPtr window, uint message, nuint wParam, IntPtr lParam);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct WindowClass
        {
            public uint Size, Style;
            public WindowProcedure Procedure;
            public int ClassExtra, WindowExtra;
            public IntPtr Instance, Icon, Cursor, Background;
            public string? MenuName;
            public string ClassName;
            public IntPtr SmallIcon;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct Message
        {
            public IntPtr Window;
            public uint Id;
            public nuint WParam;
            public IntPtr LParam;
            public uint Time;
            public int X, Y;
            public uint Private;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct IconData
        {
            public uint Size;
            public IntPtr Window;
            public uint Id, Flags, Callback;
            public IntPtr Icon;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Tip;
            public uint State, StateMask;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string Text;
            public uint Version;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string Title;
            public uint InfoFlags;
            public Guid Guid;
            public IntPtr BalloonIcon;
        }

        [DllImport("kernel32.dll", EntryPoint = "GetModuleHandleW", CharSet = CharSet.Unicode)]
        public static extern IntPtr GetModuleHandle(string? module);
        [DllImport("user32.dll", EntryPoint = "RegisterClassExW", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern ushort RegisterClassEx(ref WindowClass registration);
        [DllImport("user32.dll", EntryPoint = "CreateWindowExW", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern IntPtr CreateWindowEx(uint extended, string className, string name, uint style,
            int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr parameter);
        [DllImport("user32.dll", EntryPoint = "UnregisterClassW", CharSet = CharSet.Unicode)]
        [return: MarshalAs(UnmanagedType.Bool)] public static extern bool UnregisterClass(string className, IntPtr instance);
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static extern bool DestroyWindow(IntPtr window);
        [DllImport("user32.dll", EntryPoint = "GetMessageW", SetLastError = true)]
        public static extern int GetMessage(out Message message, IntPtr window, uint min, uint max);
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static extern bool TranslateMessage(ref Message message);
        [DllImport("user32.dll", EntryPoint = "DispatchMessageW")] public static extern IntPtr DispatchMessage(ref Message message);
        [DllImport("user32.dll", EntryPoint = "DefWindowProcW")] public static extern IntPtr DefWindowProc(IntPtr window, uint message, nuint wParam, IntPtr lParam);
        [DllImport("user32.dll")] public static extern void PostQuitMessage(int code);
        [DllImport("user32.dll", EntryPoint = "LoadIconW")] public static extern IntPtr LoadIcon(IntPtr instance, IntPtr resource);
        [DllImport("user32.dll")] public static extern nuint SetTimer(IntPtr window, nuint id, uint milliseconds, IntPtr callback);
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static extern bool KillTimer(IntPtr window, nuint id);
        [DllImport("shell32.dll", EntryPoint = "Shell_NotifyIconW", CharSet = CharSet.Unicode)]
        [return: MarshalAs(UnmanagedType.Bool)] public static extern bool ShellNotifyIcon(uint operation, ref IconData notification);
    }
}
