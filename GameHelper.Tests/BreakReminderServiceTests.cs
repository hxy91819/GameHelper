using GameHelper.Core.Abstractions;
using GameHelper.Core.Models;
using GameHelper.Core.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace GameHelper.Tests;

public sealed class BreakReminderServiceTests
{
    [Fact]
    public void ForegroundGame_FirstReminderAt40Minutes_ThenEvery10Minutes()
    {
        using var test = new Harness();
        test.Clock.AdvanceSeconds(2399);
        Assert.Empty(test.Notifier.Messages);
        test.Clock.AdvanceSeconds(1);
        Assert.Equal(TimeSpan.FromMinutes(40), Assert.Single(test.Notifier.Messages).Playtime);
        test.Clock.AdvanceSeconds(1200);
        Assert.Equal(new[] { 40d, 50d, 60d }, test.Notifier.Messages.Select(message => message.Playtime.TotalMinutes));
        Assert.All(test.Notifier.Messages, message => { Assert.Equal(5, message.BreakMinutes); Assert.True(message.Sound); });
    }

    [Fact]
    public void ShortBreak_PausesPlaytime_AndSwitchingGamesContinuesSameRound()
    {
        using var test = new Harness();
        test.Clock.AdvanceSeconds(1200);
        test.SetActivity(ForegroundActivity.Away);
        test.Clock.AdvanceSeconds(120);
        Assert.Empty(test.Notifier.Messages);
        test.SetActivity(new ForegroundActivity(true, new ProcessEventInfo("second.exe", null, 2), 200));
        test.Clock.AdvanceSeconds(1200);
        Assert.Equal(TimeSpan.FromMinutes(40), Assert.Single(test.Notifier.Messages).Playtime);
    }

    [Theory]
    [InlineData(299, true)]
    [InlineData(300, false)]
    public void ContinuousBreak_ResetsExactlyAtFiveMinutes(int breakSeconds, bool shouldNotify)
    {
        using var test = new Harness();
        test.Clock.AdvanceSeconds(2399);
        test.SetActivity(ForegroundActivity.Away);
        test.Clock.AdvanceSeconds(breakSeconds);
        test.SetActivity(Harness.Game);
        test.Clock.AdvanceSeconds(1);
        Assert.Equal(shouldNotify ? 1 : 0, test.Notifier.Messages.Count);
        if (!shouldNotify)
        {
            test.Clock.AdvanceSeconds(2399);
            Assert.Single(test.Notifier.Messages);
        }
    }

    [Fact]
    public void SeveralShortBreaks_DoNotCombineIntoACompletedBreak()
    {
        using var test = new Harness();
        for (var round = 0; round < 4; round++)
        {
            test.Clock.AdvanceSeconds(600);
            test.SetActivity(ForegroundActivity.Away);
            test.Clock.AdvanceSeconds(120);
            test.SetActivity(Harness.Game);
        }
        Assert.Single(test.Notifier.Messages);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void UnavailableSampling_PausesWithoutEstablishingBreak(bool throws)
    {
        using var test = new Harness();
        test.Clock.AdvanceSeconds(2399);
        test.Activity.Throws = throws;
        test.SetActivity(ForegroundActivity.Unavailable);
        test.Clock.AdvanceSeconds(600);
        Assert.Empty(test.Notifier.Messages);
        test.Activity.Throws = false;
        test.SetActivity(Harness.Game);
        test.Clock.AdvanceSeconds(1);
        Assert.Single(test.Notifier.Messages);
    }

    [Theory]
    [InlineData(120, true)]
    [InlineData(300, false)]
    public void Suspend_ExcludesSleepAndResetsOnlyAfterLongEnoughBreak(int sleepSeconds, bool shouldNotify)
    {
        using var test = new Harness();
        test.Clock.AdvanceSeconds(2399);
        test.Activity.Current = Harness.Game with { SuspendedFor = TimeSpan.FromSeconds(sleepSeconds) };
        test.Clock.JumpSeconds(sleepSeconds);
        test.Activity.Current = Harness.Game;
        test.Clock.AdvanceSeconds(1);
        Assert.Equal(shouldNotify ? 1 : 0, test.Notifier.Messages.Count);
    }

    [Fact]
    public void SchedulingGap_DoesNotCountUnobservedTimeAsPlayOrRest()
    {
        using var test = new Harness();
        test.Clock.AdvanceSeconds(2399);
        test.Clock.JumpSeconds(600);
        Assert.Empty(test.Notifier.Messages);
        test.SetActivity(ForegroundActivity.Away);
        test.Clock.JumpSeconds(600);
        test.SetActivity(Harness.Game);
        test.Clock.AdvanceSeconds(1);
        Assert.Single(test.Notifier.Messages);
    }

    [Fact]
    public void NotificationFailure_RetriesAtNextIntervalWithoutSpamming()
    {
        using var test = new Harness();
        test.Notifier.Throws = true;
        test.Clock.AdvanceSeconds(2400);
        Assert.Equal(1, test.Notifier.Attempts);
        test.Clock.AdvanceSeconds(599);
        Assert.Equal(1, test.Notifier.Attempts);
        test.Notifier.Throws = false;
        test.Clock.AdvanceSeconds(1);
        Assert.Equal(TimeSpan.FromMinutes(50), Assert.Single(test.Notifier.Messages).Playtime);
    }

    [Fact]
    public void ReloadCatalog_RefreshesCachedMatchWithoutResettingPlaytime()
    {
        using var test = new Harness();
        test.Clock.AdvanceSeconds(1200);
        test.Configuration.Games[0].IsEnabled = false;
        test.Service.ReloadConfig(test.Configuration);
        test.Clock.AdvanceSeconds(1);
        test.Clock.AdvanceSeconds(120);
        test.Configuration.Games[0].IsEnabled = true;
        test.Service.ReloadConfig(test.Configuration);
        test.SetActivity(Harness.Game);
        test.Clock.AdvanceSeconds(1199);
        Assert.Single(test.Notifier.Messages);
    }

    [Fact]
    public void ReusedPid_DoesNotReusePreviousProcessClassification()
    {
        using var test = new Harness();
        test.Clock.AdvanceSeconds(1200);
        test.SetActivity(new ForegroundActivity(true, new ProcessEventInfo("other.exe", null, 1), 999));
        test.Clock.AdvanceSeconds(300);
        test.SetActivity(Harness.Game);
        test.Clock.AdvanceSeconds(1200);
        Assert.Empty(test.Notifier.Messages);
    }

    [Fact]
    public void StopAndDisable_CancelOldCallbacksAndRestartWithFreshRound()
    {
        using var test = new Harness();
        test.Clock.AdvanceSeconds(2399);
        var old = Assert.Single(test.Clock.Timers);
        test.Service.Stop();
        Assert.False(test.Service.IsRunning);
        test.Clock.JumpSeconds(2400);
        old.Fire();
        Assert.Empty(test.Notifier.Messages);
        test.Service.Start(test.Configuration);
        test.Clock.AdvanceSeconds(2399);
        Assert.Empty(test.Notifier.Messages);
        test.Configuration.BreakReminder = new BreakReminderSettings();
        test.Service.ReloadConfig(test.Configuration);
        Assert.False(test.Service.IsRunning);
        test.Clock.AdvanceSeconds(600);
        Assert.Empty(test.Notifier.Messages);
    }

    [Fact]
    public void ReloadTiming_ResetsRound_AndRepeatedStartIsIdempotent()
    {
        using var test = new Harness();
        test.Service.Start(test.Configuration);
        Assert.Single(test.Clock.Timers);
        test.Clock.AdvanceSeconds(1200);
        test.Configuration.BreakReminder = new BreakReminderSettings { Enabled = true, PlayMinutes = 1, Sound = false };
        test.Service.ReloadConfig(test.Configuration);
        test.Clock.AdvanceSeconds(59);
        Assert.Empty(test.Notifier.Messages);
        test.Clock.AdvanceSeconds(1);
        Assert.False(Assert.Single(test.Notifier.Messages).Sound);
    }

    [Fact]
    public void MissingOrInvalidSettings_DisablesTimer_AndReloadCanEnableIt()
    {
        using var test = new Harness(start: false);
        test.Configuration.BreakReminder = null;
        test.Service.Start(test.Configuration);
        Assert.False(test.Service.IsRunning);
        test.Configuration.BreakReminder = new BreakReminderSettings { Enabled = true, PlayMinutes = -1 };
        test.Service.ReloadConfig(test.Configuration);
        Assert.False(test.Service.IsRunning);
        test.Configuration.BreakReminder = new BreakReminderSettings { Enabled = true };
        test.Service.ReloadConfig(test.Configuration);
        Assert.True(test.Service.IsRunning);
    }

    [Fact]
    public void Snooze_DefersNextReminderAndThenResumesRepeatInterval()
    {
        using var test = new Harness();
        test.Clock.AdvanceSeconds(2400);
        test.Service.Snooze(TimeSpan.FromMinutes(15));
        test.Clock.AdvanceSeconds(899);
        Assert.Single(test.Notifier.Messages);
        test.Clock.AdvanceSeconds(1);
        Assert.Equal(new[] { 40d, 55d }, test.Notifier.Messages.Select(message => message.Playtime.TotalMinutes));
        test.Clock.AdvanceSeconds(600);
        Assert.Equal(65, test.Notifier.Messages[^1].Playtime.TotalMinutes);
        Assert.True(test.Service.IsRunning);
    }

    [Fact]
    public void Snooze_DoesNotAdvanceExistingDeadline_AndOnlyCountsForegroundPlay()
    {
        using var test = new Harness();
        test.Service.Snooze(TimeSpan.FromMinutes(15));
        test.Clock.AdvanceSeconds(2399);
        Assert.Empty(test.Notifier.Messages);
        test.Service.Snooze(TimeSpan.FromMinutes(15));
        test.SetActivity(ForegroundActivity.Away);
        test.Clock.AdvanceSeconds(120);
        test.SetActivity(Harness.Game);
        test.Clock.AdvanceSeconds(899);
        Assert.Empty(test.Notifier.Messages);
        test.Clock.AdvanceSeconds(1);
        Assert.Single(test.Notifier.Messages);
    }

    [Fact]
    public void ResetCycle_ClearsSnoozeAndRestartsFullCycleWithoutRestartingService()
    {
        using var test = new Harness();
        test.Clock.AdvanceSeconds(2400);
        test.Service.Snooze(TimeSpan.FromHours(2));
        var clears = test.Notifier.Clears;
        test.Service.ResetCycle();
        Assert.Equal(clears + 1, test.Notifier.Clears);
        Assert.True(test.Service.IsRunning);
        Assert.Single(test.Clock.Timers);
        test.Clock.AdvanceSeconds(2399);
        Assert.Single(test.Notifier.Messages);
        test.Clock.AdvanceSeconds(1);
        Assert.Equal(40, test.Notifier.Messages[^1].Playtime.TotalMinutes);
        Assert.Equal(2, test.Notifier.Messages.Count);
    }

    [Fact]
    public void CompletedBreak_ClearsSnoozedDeadline()
    {
        using var test = new Harness();
        test.Clock.AdvanceSeconds(2400);
        test.Service.Snooze(TimeSpan.FromHours(2));
        test.SetActivity(ForegroundActivity.Away);
        test.Clock.AdvanceSeconds(300);
        test.SetActivity(Harness.Game);
        test.Clock.AdvanceSeconds(2400);
        Assert.Equal(new[] { 40d, 40d }, test.Notifier.Messages.Select(message => message.Playtime.TotalMinutes));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Snooze_RejectsNonPositiveDuration(int minutes)
    {
        using var test = new Harness();
        Assert.Throws<ArgumentOutOfRangeException>(() => test.Service.Snooze(TimeSpan.FromMinutes(minutes)));
    }

    [Fact]
    public void StoppedAndDisposed_ActionsDoNotCreateTimerOrCarryStateIntoRestart()
    {
        using var test = new Harness();
        test.Service.Stop();
        test.Service.Snooze(TimeSpan.FromHours(2));
        test.Service.ResetCycle();
        Assert.False(test.Service.IsRunning);
        test.Service.Start(test.Configuration);
        test.Clock.AdvanceSeconds(2400);
        Assert.Single(test.Notifier.Messages);
        test.Service.Dispose();
        test.Service.Snooze(TimeSpan.FromMinutes(15));
        test.Service.ResetCycle();
        Assert.False(test.Service.IsRunning);
    }

    private sealed class Harness : IDisposable
    {
        public static ForegroundActivity Game { get; } = new(true, new ProcessEventInfo("game.exe", null, 1), 100);
        public ManualClock Clock { get; } = new();
        public ActivitySource Activity { get; } = new() { Current = Game };
        public Notifier Notifier { get; } = new();
        public AppConfig Configuration { get; } = new()
        {
            BreakReminder = new BreakReminderSettings { Enabled = true },
            Games = new()
            {
                new() { DataKey = "game", Executable = "game.exe", HdrEnabled = true, SpeedMultiplier = 2 },
                new() { DataKey = "second", Executable = "second.exe" }
            }
        };
        public BreakReminderService Service { get; }

        public Harness(bool start = true)
        {
            Service = new BreakReminderService(Activity, Notifier, new Paths(), Clock, NullLogger<BreakReminderService>.Instance);
            if (start) Service.Start(Configuration);
        }
        public void SetActivity(ForegroundActivity activity)
        {
            Activity.Current = activity;
            foreach (var timer in Clock.Timers.Where(timer => !timer.Disposed)) timer.Fire();
        }
        public void Dispose() => Service.Dispose();
    }

    private sealed class ActivitySource : IForegroundActivityProvider
    {
        public ForegroundActivity Current { get; set; } = ForegroundActivity.Away;
        public bool Throws { get; set; }
        public ForegroundActivity Read() => Throws ? throw new InvalidOperationException("sample failed") : Current;
    }

    private sealed class Paths : IProcessPathResolver
    {
        public string? TryResolveExecutablePath(int processId) => null;
    }

    private sealed class Notifier : IBreakReminderNotifier
    {
        public List<(TimeSpan Playtime, int BreakMinutes, bool Sound)> Messages { get; } = new();
        public int Attempts { get; private set; }
        public bool Throws { get; set; }
        public void Show(TimeSpan playtime, int breakMinutes, bool sound)
        {
            Attempts++;
            if (Throws) throw new InvalidOperationException("notification failed");
            Messages.Add((playtime, breakMinutes, sound));
        }
        public int Clears { get; private set; }
        public void Clear() => Clears++;
        public void Dispose() { }
    }

    private sealed class ManualClock : TimeProvider
    {
        private long _timestamp;
        public List<ManualTimer> Timers { get; } = new();
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => _timestamp;
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new ManualTimer(() => callback(state));
            Timers.Add(timer);
            return timer;
        }
        public void AdvanceSeconds(int seconds)
        {
            for (var i = 0; i < seconds; i++) JumpSeconds(1);
        }
        public void JumpSeconds(int seconds)
        {
            _timestamp += TimeSpan.FromSeconds(seconds).Ticks;
            foreach (var timer in Timers.Where(timer => !timer.Disposed).ToArray()) timer.Fire();
        }
    }

    private sealed class ManualTimer(Action callback) : ITimer
    {
        public bool Disposed { get; private set; }
        public void Fire() => callback();
        public bool Change(TimeSpan dueTime, TimeSpan period) => !Disposed;
        public void Dispose() => Disposed = true;
        public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
    }
}
