using System.Security.Principal;
using GameHelper.Infrastructure.Notifications;
using GameHelper.Infrastructure.Processes;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit.Abstractions;

namespace GameHelper.Tests;

/// <summary>Opt-in desktop API checks; normal unit runs never show notifications.</summary>
public sealed class WindowsBreakReminderIntegrationTests(ITestOutputHelper output)
{
    [NativeNotificationFact]
    public void NativeNotification_SubmitClearResubmitDispose_ReleasesWindow()
    {
        using var identity = WindowsIdentity.GetCurrent();
        output.WriteLine($"Elevated: {new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator)}");
        using var notifier = new WindowsBreakReminderNotifier(NullLogger<WindowsBreakReminderNotifier>.Instance);
        Assert.False(notifier.HasWindow);
        notifier.Show(TimeSpan.FromMinutes(40), 5, sound: false);
        Assert.True(notifier.HasWindow);
        notifier.Clear();
        notifier.Show(TimeSpan.FromMinutes(50), 5, sound: true);
        Assert.True(notifier.HasWindow);
        notifier.Dispose();
        Assert.False(notifier.HasWindow);
        Assert.Throws<ObjectDisposedException>(() => notifier.Show(TimeSpan.FromMinutes(60), 5, true));
        output.WriteLine("Shell notification submission and cleanup passed; visual/audio delivery depends on system settings.");
    }

    [NativeNotificationFact]
    public void ForegroundSampler_CurrentDesktop_ProvidesAnObservation()
    {
        var provider = new WindowsForegroundActivityProvider(new WindowsProcessPathResolver(),
            NullLogger<WindowsForegroundActivityProvider>.Instance);
        var activity = provider.Read();
        output.WriteLine($"Available: {activity.IsAvailable}; away: {activity.IsAway}; process identified: {activity.Process is not null}");
        Assert.True(activity.IsAvailable, "Desktop observation unavailable in this execution session.");
        Assert.True(activity.IsAway || activity.Process is not null);
        if (activity.Process is { } process)
        {
            Assert.True(process.ProcessId > 0);
            Assert.True(activity.ProcessStartTicks > 0);
        }
    }

    [AttributeUsage(AttributeTargets.Method)]
    public sealed class NativeNotificationFactAttribute : FactAttribute
    {
        public NativeNotificationFactAttribute()
        {
            if (!OperatingSystem.IsWindows() || Environment.GetEnvironmentVariable("GAMEHELPER_VERIFY_BREAK_NOTIFICATIONS") != "1")
                Skip = "Opt in with GAMEHELPER_VERIFY_BREAK_NOTIFICATIONS=1 on an interactive Windows desktop.";
        }
    }
}
