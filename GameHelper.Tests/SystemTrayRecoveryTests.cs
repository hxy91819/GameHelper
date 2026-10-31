using GameHelper.Core.Abstractions;
using GameHelper.Core.Services;
using GameHelper.Infrastructure.Notifications;
using Microsoft.Extensions.Logging;
using Moq;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;

namespace GameHelper.Tests;

public sealed class SystemTrayRecoveryTests
{
    [WindowsOnlyFact]
    public void TaskbarRestartAndQueuedRefreshFailuresNeverFeedFailureBalloons()
    {
        var calls = new ConcurrentQueue<(uint Operation, uint Flags)>();
        var available = 1;
        bool Notify(uint operation, ref WindowsSystemTrayService.NativeMethods.IconData data)
        {
            calls.Enqueue((operation, data.Flags));
            return Volatile.Read(ref available) == 1;
        }
        using var tray = Create(Notify);
        tray.Start();
        // Use this instance's unique native window, never broadcast to Explorer/user instances.
        var field = typeof(WindowsSystemTrayService).GetField("_window",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        var window = (IntPtr)field.GetValue(tray)!;
        Assert.NotEqual(IntPtr.Zero, window);
        Volatile.Write(ref available, 0);
        var restart = RegisterWindowMessage("TaskbarCreated");
        for (var index = 0; index < 10; index++) SendMessage(window, restart, 0, IntPtr.Zero);
        Assert.DoesNotContain(calls, call => (call.Flags & 0x10) != 0);
        Volatile.Write(ref available, 1);
        SendMessage(window, restart, 0, IntPtr.Zero);
        Assert.True(tray.IsRunning);
        tray.Stop();
        Assert.Equal(2u, calls.Last().Operation);
        Assert.False(tray.IsRunning);
    }

    [WindowsOnlyFact]
    public void FailedModifyRecoversExistingIconInsteadOfGettingStuckAdding()
    {
        var calls = new List<uint>();
        var present = false;
        var failModify = false;
        bool Notify(uint operation, ref WindowsSystemTrayService.NativeMethods.IconData data)
        {
            calls.Add(operation);
            Assert.Equal(0u, data.Flags & 0x10); // No failure balloon on background refresh.
            if (operation == 2) { present = false; return true; }
            if (operation == 0) { if (present) return false; present = true; return true; }
            return !failModify && present;
        }
        using var tray = Create(Notify);
        tray.UpdateIcon(IntPtr.Zero);
        failModify = true;
        tray.UpdateIcon(IntPtr.Zero);
        failModify = false;
        tray.UpdateIcon(IntPtr.Zero);
        Assert.Equal(new uint[] { 0, 1, 2, 0, 1 }, calls);
        Assert.True(present);
    }

    [WindowsOnlyFact]
    public void PersistentFailureIsQuietAndLaterRefreshCanRecover()
    {
        var available = false;
        var calls = new List<uint>();
        var logger = new Mock<ILogger<WindowsSystemTrayService>>();
        bool Notify(uint operation, ref WindowsSystemTrayService.NativeMethods.IconData data)
        {
            Assert.Equal(0u, data.Flags & 0x10);
            calls.Add(operation);
            return available;
        }
        using var tray = Create(Notify, logger.Object);
        for (var index = 0; index < 60; index++) tray.UpdateIcon(IntPtr.Zero);
        Assert.Single(logger.Invocations.Where(i => i.Method.Name == "Log"));
        available = true;
        tray.UpdateIcon(IntPtr.Zero);
        tray.UpdateIcon(IntPtr.Zero);
        Assert.Equal(1u, calls[^1]);
        available = false;
        tray.UpdateIcon(IntPtr.Zero);
        Assert.Equal(3, logger.Invocations.Count(i => i.Method.Name == "Log")); // failure, recovery, new failure
    }

    [WindowsOnlyFact]
    public void MissingIconIsRecreatedAfterModifyFails()
    {
        var calls = new List<uint>();
        bool Notify(uint operation, ref WindowsSystemTrayService.NativeMethods.IconData data)
        {
            calls.Add(operation);
            return operation != 1;
        }
        using var tray = Create(Notify);
        tray.UpdateIcon(IntPtr.Zero);
        tray.UpdateIcon(IntPtr.Zero);
        Assert.Equal(new uint[] { 0, 1, 2, 0 }, calls);
    }

    private static WindowsSystemTrayService Create(WindowsSystemTrayService.NotifyIcon notify,
        ILogger<WindowsSystemTrayService>? logger = null) => new(() => "GameHelper",
        new TrayMenuService(Mock.Of<IGameLibrary>(), Mock.Of<ITrayHdrControl>(),
            Mock.Of<IBreakReminderService>(), TimeProvider.System, () => { }, () => { }),
        logger ?? Mock.Of<ILogger<WindowsSystemTrayService>>(), notify);

    [DllImport("user32.dll", EntryPoint = "RegisterWindowMessageW", CharSet = CharSet.Unicode)]
    private static extern uint RegisterWindowMessage(string name);
    [DllImport("user32.dll", EntryPoint = "SendMessageW")]
    private static extern IntPtr SendMessage(IntPtr window, uint message, nuint wParam, IntPtr lParam);
}
