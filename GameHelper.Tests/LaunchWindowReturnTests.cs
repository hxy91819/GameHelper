using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using GameHelper.Core.Abstractions;
using GameHelper.Core.Models;
using GameHelper.Desktop;
using GameHelper.Infrastructure.Processes;

namespace GameHelper.Tests;

public sealed class LaunchWindowReturnTests
{
    [Fact]
    public Task DesktopLaunchReturnsOnlyAfterItsConfirmedSessionEnds() => OnDispatcher(async () =>
    {
        var backend = new Backend();
        using var model = new LibraryViewModel(backend);
        var minimized = 0; var restored = 0;
        var handoff = new MainWindowLaunchWindowHandoff(() => minimized++, () => restored++, TimeProvider.System);
        model.LaunchWindowHandoff = handoff;
        await model.RefreshAsync();
        model.LaunchCommand.Execute(null);
        Assert.NotNull(backend.RequestId);
        Assert.Equal(1, minimized);
        Assert.True(handoff.Tracking);
        await model.RefreshAsync(); Assert.Equal(0, restored);
        backend.Running = false;
        await model.RefreshAsync(); await model.RefreshAsync();
        Assert.Equal(1, restored); Assert.False(handoff.Tracking);
    });

    [Fact]
    public void ExternalAndReplacedRequestsCannotRestoreTheWindow()
    {
        var restored = 0;
        var handoff = new MainWindowLaunchWindowHandoff(() => { }, () => restored++, TimeProvider.System);
        handoff.Observe([Row(true, Guid.NewGuid(), 1)]);
        handoff.Observe([]); Assert.Equal(0, restored);
        var request = Guid.NewGuid(); handoff.Track("game", "Game", request);
        handoff.Observe([Row(true, Guid.NewGuid(), 1)]); handoff.Observe([]);
        Assert.Equal(0, restored);
        var latest = Guid.NewGuid(); handoff.Track("game", "Game", latest);
        handoff.Observe([Row(true, request, 1)]); handoff.Observe([]);
        Assert.Equal(0, restored);
        handoff.Observe([Row(true, latest, 2)]);
        handoff.Observe([Row(true, latest, 2)]); Assert.Equal(0, restored);
        handoff.Observe([]); handoff.Observe([]); Assert.Equal(1, restored);
    }

    [Fact]
    public Task InactiveWindowHandoffCannotRestoreHiddenDesktop() => OnDispatcher(async () =>
    {
        var backend = new Backend();
        using var model = new LibraryViewModel(backend);
        var restored = 0;
        var desktop = new MainWindowLaunchWindowHandoff(() => { }, () => restored++, TimeProvider.System);
        model.LaunchWindowHandoff = desktop;
        await model.RefreshAsync(); model.LaunchCommand.Execute(null);
        model.LaunchWindowHandoff = NoopLaunchWindowHandoff.Instance;
        backend.Running = false;
        await model.RefreshAsync(); Assert.Equal(0, restored);
    });

    [ForegroundRestoreTheory]
    [InlineData(false)]
    [InlineData(true)]
    public Task SurrogateExitRestoresNativeForegroundAndKeyboardFocus(bool collapsedTarget) => OnDispatcher(async () =>
    {
        var root = Path.Combine(Path.GetTempPath(), "GH-Foreground-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var field = new TextBox { Text = "隔离前台恢复验证", Margin = new Thickness(20) };
        var window = new Window { Title = "GameHelper isolated return probe", Width = 430, Height = 180, Content = field };
        Process? surrogate = null;
        try
        {
            window.Show();
            Assert.True(WindowForegroundActivation.Restore(window, field), "The isolated destination must first acquire native foreground rights.");
            WindowsForegroundPermission.AllowAnyProcess();
            var script = Path.Combine(root, "surrogate.ps1");
            File.WriteAllText(script, """
                param([string]$ProbeRoot)
                Add-Type -AssemblyName PresentationFramework
                Add-Type -TypeDefinition @'
                using System;
                using System.Runtime.InteropServices;
                public static class ProbeForeground {
                  [DllImport("kernel32.dll")] static extern uint GetCurrentThreadId();
                  [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
                  [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, IntPtr p);
                  [DllImport("user32.dll")] static extern bool AttachThreadInput(uint a, uint b, bool attach);
                  [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr h);
                  public static void Activate(IntPtr h) {
                    uint current=GetCurrentThreadId(), foreground=GetWindowThreadProcessId(GetForegroundWindow(),IntPtr.Zero);
                    bool attached=foreground!=0 && foreground!=current && AttachThreadInput(current,foreground,true);
                    try { SetForegroundWindow(h); } finally { if(attached) AttachThreadInput(current,foreground,false); }
                  }
                }
                '@
                $probeWindow=New-Object Windows.Window
                $probeWindow.Title='GameHelper isolated surrogate game'
                $probeWindow.Width=430; $probeWindow.Height=180
                $probeWindow.Content='隔离游戏替身：自动退出，不修改游戏或配置'
                $probeWindow.Add_ContentRendered({
                    $probeWindow.Activate() | Out-Null
                    $handle=(New-Object Windows.Interop.WindowInteropHelper($probeWindow)).Handle
                    [ProbeForeground]::Activate($handle)
                    [IO.File]::WriteAllText((Join-Path $ProbeRoot 'ready'),$handle.ToInt64().ToString())
                })
                $timer=New-Object Windows.Threading.DispatcherTimer
                $timer.Interval=[TimeSpan]::FromMilliseconds(100)
                $deadline=[DateTime]::UtcNow.AddSeconds(20)
                $timer.Add_Tick({ if ((Test-Path (Join-Path $ProbeRoot 'exit')) -or [DateTime]::UtcNow -ge $deadline) { $timer.Stop(); $probeWindow.Close() } })
                $timer.Start(); $probeWindow.ShowDialog() | Out-Null
                """);
            var start = new ProcessStartInfo("powershell.exe") { UseShellExecute = false, CreateNoWindow = true };
            foreach (var arg in new[] { "-NoProfile", "-STA", "-WindowStyle", "Hidden", "-File", script, "-ProbeRoot", root }) start.ArgumentList.Add(arg);
            surrogate = Process.Start(start)!;
            var request = Guid.NewGuid();
            var handoff = new MainWindowLaunchWindowHandoff(window, field);
            if (collapsedTarget) field.Visibility = Visibility.Collapsed;
            handoff.Track("game", "Surrogate", request);
            handoff.Observe([Row(true, request, surrogate.Id)]);
            handoff.Minimize();
            await Until(() => File.Exists(Path.Combine(root, "ready")));
            var surrogateHandle = (nint)long.Parse(File.ReadAllText(Path.Combine(root, "ready")));
            await Until(() => GetForegroundWindow() == surrogateHandle);
            Assert.NotEqual(new WindowInteropHelper(window).Handle, GetForegroundWindow());
            File.WriteAllText(Path.Combine(root, "exit"), "exit");
            await surrogate.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            handoff.Observe([]);
            var expected = new WindowInteropHelper(window).Handle;
            var expectedFocus = collapsedTarget ? (IInputElement)window : field;
            await Until(() => GetForegroundWindow() == expected && Keyboard.FocusedElement == expectedFocus);
            Assert.Equal(expected, GetForegroundWindow());
            Assert.Same(expectedFocus, Keyboard.FocusedElement);
            Assert.True(window.IsKeyboardFocusWithin);
            Assert.Equal(WindowState.Normal, window.WindowState);
            var info = new GuiThreadInfo { Size = Marshal.SizeOf<GuiThreadInfo>() };
            Assert.True(GetGUIThreadInfo(0, ref info));
            Assert.Equal(expected, info.Active);
            Assert.Equal(expected, info.Focus);
        }
        finally
        {
            if (surrogate is not null) { if (!surrogate.HasExited) surrogate.Kill(); surrogate.Dispose(); }
            window.Close();
            Directory.Delete(root, true);
        }
    });

    private static async Task Until(Func<bool> condition)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!condition()) await Task.Delay(30, deadline.Token);
    }
    private static Task OnDispatcher(Func<Task> action)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            dispatcher.BeginInvoke(new Action(async () =>
            {
                try { await action(); completion.SetResult(); }
                catch (Exception ex) { completion.SetException(ex); }
                finally { dispatcher.BeginInvokeShutdown(DispatcherPriority.Background); }
            }));
            Dispatcher.Run();
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        return completion.Task.WaitAsync(TimeSpan.FromSeconds(35));
    }
    private static LibraryGame Row(bool running, Guid? requestId, int? pid) => new()
    {
        Config = new() { DataKey = "game", Executable = @"C:\isolated-game.exe", DisplayName = "Game" },
        State = running ? "运行中" : "未运行", LaunchRequestId = requestId, LaunchProcessId = pid,
        Installation = new(GameInstallationState.Installed, "test")
    };
    private sealed class Backend : IGameLibrary
    {
        internal Guid? RequestId;
        internal bool Running;
        public Task<GameLibraryResponse> ExecuteAsync(GameLibraryRequest request, CancellationToken cancellationToken = default)
        {
            if (request.Operation == GameLibraryOperation.Launch) { RequestId = request.LaunchRequestId; Running = true; }
            return Task.FromResult(new GameLibraryResponse { Success = true, Games = [Row(Running, Running ? RequestId : null, Running ? 42 : null)] });
        }
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct GuiThreadInfo
    {
        public int Size; public uint Flags; public nint Active, Focus, Capture, MenuOwner, MoveSize, Caret;
        public int Left, Top, Right, Bottom;
    }
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetGUIThreadInfo(uint thread, ref GuiThreadInfo info);
}

public sealed class ForegroundRestoreTheoryAttribute : TheoryAttribute
{
    public ForegroundRestoreTheoryAttribute()
    {
        if (!OperatingSystem.IsWindows() || Environment.GetEnvironmentVariable("GAMEHELPER_TEST_FOREGROUND") != "1")
            Skip = "Opt in with GAMEHELPER_TEST_FOREGROUND=1: briefly opens isolated return/surrogate windows.";
    }
}
