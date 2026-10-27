using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using GameHelper.Core.Abstractions;
using GameHelper.Core.Models;
using GameHelper.Core.Services;
using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace GameHelper.Infrastructure.Notifications;


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
    private readonly TrayMenuService _menus;
    private readonly ConcurrentQueue<Action> _updates = new();
    private readonly SemaphoreSlim _operations = new(1, 1);
    private string _cachedSummary = "GameHelper";
    private int _refreshing;
    private int _openingMenu;
    private long _generation;
    private CancellationTokenSource _lifetime = new();
    private const uint UpdateMessage = 0x8002;
    private readonly ILogger<WindowsSystemTrayService> _logger;
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

    public WindowsSystemTrayService(Func<string> summary, TrayMenuService menus,
        ILogger<WindowsSystemTrayService> logger)
    {
        _summary = summary;
        _menus = menus;
        _logger = logger;
    }

    public bool IsRunning => !_stopRequested && Volatile.Read(ref _window) != IntPtr.Zero;

    public void Start()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (IsRunning) return;
            if (_thread?.IsAlive == true) throw new InvalidOperationException("Previous tray thread is still stopping.");
            _lifetime.Dispose();
            _lifetime = new();
            Interlocked.Increment(ref _generation);
            Volatile.Write(ref _openingMenu, 0);
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
            SafeCleanup(_lifetime.Cancel);
            Interlocked.Increment(ref _generation);
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
            _lifetime.Dispose();
            if (_thread?.IsAlive != true) _ready.Dispose();
        }
    }

    private void RunWindow()
    {
        NativeMessageWindow? messageWindow = null;
        IntPtr window = IntPtr.Zero;
        try
        {
            _taskbarCreated = NativeMethods.RegisterWindowMessage("TaskbarCreated");
            if (_taskbarCreated == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
            messageWindow = new NativeMessageWindow(_className, "GameHelper", HandleMessage,
                ex => _logger.LogWarning(ex, "Failed to clean up native message window"));
            window = messageWindow.Handle;
            // Elevated monitoring still needs Explorer's lower-integrity restart broadcast.
            if (!NativeMethods.ChangeWindowMessageFilterEx(window, _taskbarCreated, 1, IntPtr.Zero))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            UpdateIcon(window);
            if (NativeMethods.SetTimer(window, 1, 5000, IntPtr.Zero) == 0)
                throw new Win32Exception(Marshal.GetLastWin32Error());
            Volatile.Write(ref _window, window);
            _ready.Set();
            RefreshSummary(window);
            if (_stopRequested) return;
            NativeMessageWindow.PumpMessages();
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
            messageWindow?.Dispose();
            Volatile.Write(ref _window, IntPtr.Zero);
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
                case UpdateMessage:
                    while (_updates.TryDequeue(out var update)) update();
                    return IntPtr.Zero;
                case 0x0113: RefreshSummary(window); return IntPtr.Zero; // WM_TIMER
                case CallbackMessage:
                    var eventId = unchecked((uint)lParam.ToInt64()) & 0xffff;
                    if (eventId == 0x0203) Dispatch(new("打开游戏库", TrayMenuAction.OpenLibrary, true), window); // WM_LBUTTONDBLCLK
                    else if (eventId is 0x0205 or 0x007b) RequestMenu(window); // WM_RBUTTONUP / WM_CONTEXTMENU
                    return IntPtr.Zero;
                case 0x0010: NativeMessageWindow.DestroyWindow(window); return IntPtr.Zero;
                case 0x0002: NativeMessageWindow.PostQuitMessage(0); return IntPtr.Zero;
            }
        }
        catch (Exception ex) { ReportFailure(window, ex); return IntPtr.Zero; }
        return NativeMessageWindow.DefWindowProc(window, message, wParam, lParam);
    }

    private void RefreshSummary(IntPtr window)
    {
        if (Interlocked.Exchange(ref _refreshing, 1) != 0) return;
        var generation = Volatile.Read(ref _generation);
        _ = Task.Run(() =>
        {
            try
            {
                var summary = _summary();
                PostUpdate(window, generation, () => { _cachedSummary = summary; UpdateIcon(window); });
            }
            catch (Exception ex) { ReportFailure(window, ex, generation); }
            finally { Volatile.Write(ref _refreshing, 0); }
        });
    }

    private void RequestMenu(IntPtr window)
    {
        if (Interlocked.Exchange(ref _openingMenu, 1) != 0) return;
        var generation = Volatile.Read(ref _generation);
        var token = _lifetime.Token;
        _ = Task.Run(async () =>
        {
            try
            {
                var items = await _menus.GetMenuAsync(token).ConfigureAwait(false);
                PostUpdate(window, generation, () =>
                {
                    try { ShowMenu(window, items); }
                    finally { Volatile.Write(ref _openingMenu, 0); }
                });
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { }
            catch (Exception ex)
            {
                ReportFailure(window, ex, generation);
                Volatile.Write(ref _openingMenu, 0);
            }
        });
    }

    private void Dispatch(TrayMenuItem item, IntPtr window)
    {
        var generation = Volatile.Read(ref _generation);
        var token = _lifetime.Token;
        _ = Task.Run(async () =>
        {
            await _operations.WaitAsync().ConfigureAwait(false);
            try
            {
                if (_stopRequested || window != Volatile.Read(ref _window) || generation != Volatile.Read(ref _generation)) return;
                await _menus.DispatchAsync(item, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { }
            catch (Exception ex) { ReportFailure(window, ex, generation); }
            finally { _operations.Release(); }
        });
    }

    private void PostUpdate(IntPtr window, long generation, Action action)
    {
        if (_stopRequested || window != Volatile.Read(ref _window) || generation != Volatile.Read(ref _generation)) return;
        _updates.Enqueue(() =>
        {
            if (!_stopRequested && window == Volatile.Read(ref _window) && generation == Volatile.Read(ref _generation)) action();
        });
        NativeMethods.PostMessage(window, UpdateMessage, 0, IntPtr.Zero);
    }

    private void ReportFailure(IntPtr window, Exception exception, long? generation = null)
    {
        var origin = generation ?? Volatile.Read(ref _generation);
        _ = Task.Run(() =>
        {
            _logger.LogWarning(exception, "Tray operation failed");
            PostUpdate(window, origin, () =>
            {
                var data = IconData(window);
                data.Flags = 0x10; // NIF_INFO
                data.Title = "GameHelper 操作失败";
                data.Text = exception.Message.Length > 255 ? exception.Message[..255] : exception.Message;
                data.InfoFlags = 3;
                NativeMethods.ShellNotifyIcon(1, ref data);
            });
        });
    }

    private void ShowMenu(IntPtr window, IReadOnlyList<TrayMenuItem> items)
    {
        var menu = NativeMethods.CreatePopupMenu();
        if (menu == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
        try
        {
            for (var index = 0; index < items.Count; index++)
            {
                var item = items[index];
                Append(menu, item.Enabled ? 0u : 1u, (uint)index + 1, item.Text.Replace("&", "&&"));
            }
            if (!NativeMethods.GetCursorPos(out var point)) throw new Win32Exception(Marshal.GetLastWin32Error());
            NativeMethods.SetForegroundWindow(window);
            var command = NativeMethods.TrackPopupMenuEx(menu, 0x0100 | 0x0002, point.X, point.Y, window, IntPtr.Zero);
            NativeMethods.PostMessage(window, 0, 0, IntPtr.Zero);
            if (command > 0 && command <= items.Count) Dispatch(items[(int)command - 1], window);
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
        Tip = SystemTrayLogic.Tooltip(_cachedSummary), Text = string.Empty, Title = string.Empty
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

        [DllImport("user32.dll", EntryPoint = "LoadIconW")] public static extern IntPtr LoadIcon(IntPtr instance, IntPtr resource);
        [DllImport("user32.dll")] public static extern nuint SetTimer(IntPtr window, nuint id, uint milliseconds, IntPtr callback);
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static extern bool KillTimer(IntPtr window, nuint id);
        [DllImport("shell32.dll", EntryPoint = "Shell_NotifyIconW", CharSet = CharSet.Unicode)]
        [return: MarshalAs(UnmanagedType.Bool)] public static extern bool ShellNotifyIcon(uint operation, ref IconData notification);
    }
}
