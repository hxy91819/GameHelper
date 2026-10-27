using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using GameHelper.Core.Abstractions;
using GameHelper.Core.Models;
using GameHelper.Core.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Moq;
using CoreGameConfig = GameHelper.Core.Models.GameConfig;

namespace GameHelper.Tests
{
    // Fakes
    file sealed class FakeMonitor : IProcessMonitor
    {
        public event Action<ProcessEventInfo>? ProcessStarted;
        public event Action<ProcessEventInfo>? ProcessStopped;
        public ProcessObservationPolicy LastPolicy { get; private set; } = ProcessObservationPolicy.ObserveAll();
        public void Start() { }
        public void Stop() { }
        public void Dispose() { }
        public void Configure(ProcessObservationPolicy policy) => LastPolicy = policy;
        public void RaiseStart(ProcessEventInfo info) => ProcessStarted?.Invoke(info);
        public void RaiseStop(ProcessEventInfo info) => ProcessStopped?.Invoke(info);
    }

    file sealed class FakeHdr : IHdrController
    {
        public int EnableCalls { get; private set; }
        public int DisableCalls { get; private set; }
        public bool IsEnabled { get; private set; }
        public void Enable() { IsEnabled = true; EnableCalls++; }
        public void Disable() { IsEnabled = false; DisableCalls++; }
        public void SetState(bool enabled) { IsEnabled = enabled; }
    }

    file sealed class FakePlayTime : IPlayTimeService
    {
        public int StartCalls { get; private set; }
        public int StopCalls { get; private set; }
        public List<string> Started { get; } = new();
        public List<string> Stopped { get; } = new();
        public List<TimeSpan> ShiftedStarts { get; } = new();
        public void StartTracking(string gameName) { StartCalls++; Started.Add(gameName); }
        public void ResumeTracking(string gameName, DateTime startTime) { StartCalls++; Started.Add(gameName); }
        public void WriteRecoveredSession(string gameName, DateTime startTime, DateTime endTime) { StopCalls++; Stopped.Add(gameName); }
        public void ShiftActiveStarts(TimeSpan offset) => ShiftedStarts.Add(offset);
        public PlaySession? StopTracking(string gameName)
        {
            StopCalls++;
            Stopped.Add(gameName);
            return new PlaySession(gameName, DateTime.MinValue, DateTime.MinValue, TimeSpan.Zero, 0);
        }
    }

    file sealed class FakePowerEvents : ISystemPowerEvents
    {
        public event EventHandler? Suspending;
        public event EventHandler? Resumed;
        public void RaiseSuspend() => Suspending?.Invoke(this, EventArgs.Empty);
        public void RaiseResume() => Resumed?.Invoke(this, EventArgs.Empty);
    }

    file sealed class PowerClock : TimeProvider
    {
        private DateTimeOffset _utcNow;
        public PowerClock(DateTimeOffset utcNow) => _utcNow = utcNow;
        public void Advance(TimeSpan duration) => _utcNow += duration;
        public override DateTimeOffset GetUtcNow() => _utcNow;
    }

    file sealed class FakeSpeedController : IGameSpeedController
    {
        public List<(int ProcessId, double Speed)> Applied { get; } = new();
        public IReadOnlyList<SpeedHotkey>? Hotkeys { get; private set; }
        public bool ApplySpeed(int processId, double speedMultiplier, IReadOnlyList<SpeedHotkey> hotkeys)
        {
            Hotkeys = hotkeys;
            return ApplySpeed(processId, speedMultiplier);
        }
        private readonly TaskCompletionSource _firstApply =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>Awaiting this is the reliable way to observe an async-gated attach in tests.</summary>
        public Task WaitForFirstApplyAsync(TimeSpan timeout) => _firstApply.Task.WaitAsync(timeout);

        public bool ApplySpeed(int processId, double speedMultiplier)
        {
            Applied.Add((processId, speedMultiplier));
            _firstApply.TrySetResult();
            return true;
        }
    }

    file sealed class FakeLauncher : IGameLauncher
    {
        public int Starts { get; private set; }
        public List<string> Companions { get; } = [];
        public Action? OnCompanion { get; set; }
        public int CompanionStops { get; private set; }
        public void StopCompanion() { CompanionStops++; OnCompanion?.Invoke(); }
        public void EnsureCompanionRunning(string path) { Companions.Add(path); OnCompanion?.Invoke(); }
        public void Validate(GameLaunchOptions options) { }
        public void Start(GameLaunchOptions options) { Starts++; }
    }

    file sealed class FakePlayTimeWithDuration : IPlayTimeService
    {
        private readonly DateTime _startTime;
        private readonly TimeSpan _duration;

        public FakePlayTimeWithDuration(DateTime startTime, TimeSpan duration)
        {
            _startTime = startTime;
            _duration = duration;
        }

        public void StartTracking(string gameName) { }

        public void ResumeTracking(string gameName, DateTime startTime) { }

        public void WriteRecoveredSession(string gameName, DateTime startTime, DateTime endTime) { }

        public void ShiftActiveStarts(TimeSpan offset) { }

        public PlaySession? StopTracking(string gameName)
        {
            var endTime = _startTime + _duration;
            var durationMinutes = (long)Math.Round(_duration.TotalMinutes);
            return new PlaySession(gameName, _startTime, endTime, _duration, durationMinutes);
        }
    }

    file sealed class AttachBarrierLogger : ILogger<GameAutomationService>, IDisposable
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Cancelled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ManualResetEventSlim Release { get; } = new();
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel level) => true;
        public void Log<TState>(LogLevel level, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            var message = formatter(state, exception);
            if (message.Contains("attaching speed now", StringComparison.Ordinal))
            {
                Entered.TrySetResult();
                if (!Release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("Attach barrier was not released");
            }
            if (message.Contains("Deferred speed attach cancelled", StringComparison.Ordinal)) Cancelled.TrySetResult();
        }
        public void Dispose() => Release.Dispose();
    }

    file sealed class ListLogger<T> : ILogger<T>
    {
        private sealed class NullScope : IDisposable
        {
            public static NullScope Instance { get; } = new();
            public void Dispose() { }
        }

        public List<(LogLevel Level, string Message)> Entries { get; } = new();

        public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (formatter is null)
            {
                return;
            }

            Entries.Add((logLevel, formatter(state, exception)));
        }
    }

    file static class TestConfigDocument
    {
        public static AppConfig From(IReadOnlyDictionary<string, CoreGameConfig> configs, MonitorModeSettings? monitorMode = null) =>
            new() { Games = configs.Values.Select(CloneGame).ToList(), MonitorMode = monitorMode };

        public static AppConfig Clone(AppConfig source) => new()
        {
            Games = source.Games.Select(CloneGame).ToList(),
            ProcessMonitorType = source.ProcessMonitorType,
            LaunchOnSystemStartup = source.LaunchOnSystemStartup,
            MonitorMode = source.MonitorMode,
            CheatEngine = source.CheatEngine
        };

        private static CoreGameConfig CloneGame(CoreGameConfig game) => new()
        {
            DataKey = game.DataKey,
            Executable = game.Executable,
            DisplayName = game.DisplayName,
            IsEnabled = game.IsEnabled,
            HdrEnabled = game.HdrEnabled,
            SpeedMultiplier = game.SpeedMultiplier,
            SpeedAttach = game.SpeedAttach,
            SpeedHotkeys = game.SpeedHotkeys,
            Launch = game.Launch
        };
    }

    file class FakeConfig : IGameConfiguration
    {
        private readonly object _sync = new();
        private AppConfig _config;

        public FakeConfig(IReadOnlyDictionary<string, CoreGameConfig> configs, MonitorModeSettings? monitorMode = null)
        {
            _config = TestConfigDocument.From(configs, monitorMode);
        }

        public AppConfig Read()
        {
            lock (_sync) return TestConfigDocument.Clone(_config);
        }

        public AppConfig Change(Action<AppConfig> change)
        {
            lock (_sync)
            {
                var next = TestConfigDocument.Clone(_config);
                change(next);
                _config = next;
                return TestConfigDocument.Clone(_config);
            }
        }
    }

    file sealed class MutableFakeConfig : FakeConfig
    {
        public MutableFakeConfig(IReadOnlyDictionary<string, CoreGameConfig> initial) : base(initial) { }

        public void Set(IReadOnlyDictionary<string, CoreGameConfig> configs)
        {
            Change(config => config.Games = configs.Values.Select(game => new CoreGameConfig
            {
                DataKey = game.DataKey,
                Executable = game.Executable,
                DisplayName = game.DisplayName,
                IsEnabled = game.IsEnabled,
                HdrEnabled = game.HdrEnabled,
                SpeedMultiplier = game.SpeedMultiplier
            }).ToList());
        }
    }

    public class GameAutomationServiceTests
    {
        [Fact]
        public async Task Stop_CallbackPastCancellationCheck_CannotAttachOrRepopulateRecords()
        {
            using var logger = new AttachBarrierLogger();
            var applied = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var speed = new Mock<IGameSpeedController>();
            speed.Setup(c => c.ApplySpeed(It.IsAny<int>(), It.IsAny<double>())).Returns(() =>
            {
                applied.TrySetResult();
                return true;
            });
            var coordinator = new SpeedAttachmentCoordinator(speed.Object, TimeProvider.System, logger, null,
                TimeSpan.FromMilliseconds(10), TimeSpan.FromSeconds(30));
            var exePath = CreateModdedGameDirectory("Stopped.exe");
            var directory = Path.GetDirectoryName(exePath)!;
            try
            {
                coordinator.ApplyConfiguredSpeed(new CoreGameConfig { DataKey = "stopped", SpeedMultiplier = 2 },
                    151, exePath, DateTime.UtcNow.AddMinutes(-1));
                AppendChainloaderMarker(exePath);
                await logger.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                coordinator.CancelAll();
                coordinator.ClearRecords();
                logger.Release.Set();
                // Observe the completed cancellation path, or the buggy attach, before assertions.
                await Task.WhenAny(logger.Cancelled.Task, applied.Task)
                    .WaitAsync(TimeSpan.FromSeconds(5));
                speed.Verify(c => c.ApplySpeed(It.IsAny<int>(), It.IsAny<double>()), Times.Never);
                Assert.Null(coordinator.CaptureExit(151));
            }
            finally
            {
                logger.Release.Set();
                coordinator.CancelAll();
                Directory.Delete(directory, recursive: true);
            }
        }

        [Fact]
        public async Task Stop_DeferredCallbackPastCancellationCheck_RemainsInactiveAfterRestart()
        {
            using var logger = new AttachBarrierLogger();
            var speed = new FakeSpeedController();
            var monitor = new FakeMonitor();
            var exePath = CreateModdedGameDirectory("Stopped.exe");
            var config = new CoreGameConfig { DataKey = "stopped", Executable = "Stopped.exe", IsEnabled = true, SpeedMultiplier = 2 };
            var identity = new Mock<IProcessIdentityProbe>();
            var processStartedUtc = DateTime.UtcNow.AddMinutes(-1);
            string? imagePath = exePath;
            identity.Setup(p => p.TryGetStartTimeUtc(151, out processStartedUtc)).Returns(true);
            identity.Setup(p => p.TryGetImagePath(151, out imagePath)).Returns(true);
            var service = new GameAutomationService(monitor,
                new FakeConfig(new Dictionary<string, CoreGameConfig> { ["stopped"] = config }),
                new FakeHdr(), new FakePlayTime(), logger, speedController: speed,
                speedGatePollInterval: TimeSpan.FromMilliseconds(10), processIdentityProbe: identity.Object);
            try
            {
                service.Start();
                monitor.RaiseStart(new ProcessEventInfo("Stopped.exe", exePath, 151));
                AppendChainloaderMarker(exePath);
                await logger.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                service.Stop();
                Assert.Empty(service.GetGameStates());
                service.Start();
                logger.Release.Set();
                await logger.Cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
                Assert.Empty(speed.Applied);
                Assert.Empty(service.GetGameStates());
            }
            finally
            {
                logger.Release.Set();
                service.Stop();
                DeleteDirectoryIfExists(Path.GetDirectoryName(exePath));
            }
        }

        [Fact]
        public async Task Stop_CallbackPastFinalTokenCheck_CannotBorrowRestartedGeneration()
        {
            var monitor = new FakeMonitor();
            var speed = new FakeSpeedController();
            var exePath = CreateModdedGameDirectory("OldGeneration.exe");
            var service = new GameAutomationService(monitor,
                new FakeConfig(new Dictionary<string, CoreGameConfig>
                {
                    ["old"] = new() { DataKey = "old", Executable = "OldGeneration.exe", IsEnabled = true, SpeedMultiplier = 2 }
                }), new FakeHdr(), new FakePlayTime(), NullLogger<GameAutomationService>.Instance,
                speedController: speed, speedGatePollInterval: TinyPollInterval, speedGateTimeout: TimeSpan.Zero);
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var release = new ManualResetEventSlim();
            service.Start();
            var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
            var coordinator = (SpeedAttachmentCoordinator)typeof(GameAutomationService).GetField("_speedAttachment", flags)!.GetValue(service)!;
            var scheduler = typeof(SpeedAttachmentCoordinator).GetField("_speedAttachGate", flags)!.GetValue(coordinator)!;
            var applyField = typeof(SpeedAttachScheduler).GetField("_apply", flags)!;
            var original = (Action<SpeedAttachRequest, long>)applyField.GetValue(scheduler)!;
            // Wrap the real delegate: InvokeApply has already performed its final token check,
            // but the coordinator has not examined generation or entered the controller yet.
            applyField.SetValue(scheduler, (Action<SpeedAttachRequest, long>)((request, generation) =>
            {
                entered.TrySetResult();
                if (!release.Wait(TimeSpan.FromSeconds(15))) throw new TimeoutException("Callback barrier was not released");
                try { original(request, generation); }
                finally { completed.TrySetResult(); }
            }));
            try
            {
                monitor.RaiseStart(new ProcessEventInfo("OldGeneration.exe", exePath, 155));
                await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                await Task.Run(() => Assert.Throws<TimeoutException>(service.Stop)).WaitAsync(TimeSpan.FromSeconds(8));
                service.Start();
                release.Set();
                await completed.Task.WaitAsync(TimeSpan.FromSeconds(5));
                Assert.Empty(speed.Applied);
                Assert.Null(coordinator.CaptureExit(155));
                Assert.Empty(service.GetGameStates());
            }
            finally
            {
                release.Set();
                service.Stop();
                DeleteDirectoryIfExists(Path.GetDirectoryName(exePath));
            }
        }

        [Fact]
        public async Task Stop_BlockedController_TimesOutAfterCleanup_AndLateCompletionCannotRestoreRecords()
        {
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var release = new ManualResetEventSlim();
            var speed = new Mock<IGameSpeedController>();
            speed.Setup(c => c.ApplySpeed(It.IsAny<int>(), It.IsAny<double>())).Returns(() =>
            {
                entered.TrySetResult();
                if (!release.Wait(TimeSpan.FromSeconds(15))) throw new TimeoutException("Controller barrier was not released");
                return true;
            });
            var exePath = CreateModdedGameDirectory("Blocked.exe");
            var coordinator = new SpeedAttachmentCoordinator(speed.Object, TimeProvider.System,
                NullLogger.Instance, null, TimeSpan.FromMilliseconds(10), TimeSpan.FromSeconds(30));
            try
            {
                coordinator.ApplyConfiguredSpeed(new CoreGameConfig { DataKey = "blocked", SpeedMultiplier = 2 },
                    152, exePath, DateTime.UtcNow.AddMinutes(-1));
                AppendChainloaderMarker(exePath);
                await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                var stop = Task.Run(() => Assert.Throws<TimeoutException>(coordinator.CancelAll));
                await stop.WaitAsync(TimeSpan.FromSeconds(8));
                coordinator.ClearRecords();
                release.Set();
                // A second drain waits for the previously blocked controller and record publication.
                await Task.Run(coordinator.CancelAll).WaitAsync(TimeSpan.FromSeconds(5));
                Assert.Null(coordinator.CaptureExit(152));
            }
            finally
            {
                release.Set();
                coordinator.CancelAll();
                DeleteDirectoryIfExists(Path.GetDirectoryName(exePath));
            }
        }

        [Fact]
        public async Task Stop_ControllerDrainTimeout_StillFlushesSessionsAndClearsState()
        {
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var release = new ManualResetEventSlim();
            var speed = new Mock<IGameSpeedController>();
            speed.Setup(c => c.ApplySpeed(It.IsAny<int>(), It.IsAny<double>())).Returns(() =>
            {
                entered.TrySetResult();
                Assert.True(release.Wait(TimeSpan.FromSeconds(15)));
                return true;
            });
            var monitor = new FakeMonitor();
            var playTime = new FakePlayTime();
            var store = new Mock<IInFlightSessionStore>();
            var exePath = CreateModdedGameDirectory("Blocked.exe");
            var service = new GameAutomationService(monitor,
                new FakeConfig(new Dictionary<string, CoreGameConfig>
                {
                    ["blocked"] = new() { DataKey = "blocked", Executable = "Blocked.exe", IsEnabled = true, SpeedMultiplier = 2 }
                }), new FakeHdr(), playTime, NullLogger<GameAutomationService>.Instance,
                speedController: speed.Object, inFlightSessionStore: store.Object,
                speedGatePollInterval: TimeSpan.FromMilliseconds(10), speedGateTimeout: TimeSpan.Zero);
            try
            {
                service.Start();
                monitor.RaiseStart(new ProcessEventInfo("Blocked.exe", exePath, 154));
                await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                await Task.Run(() => Assert.Throws<TimeoutException>(service.Stop)).WaitAsync(TimeSpan.FromSeconds(8));
                Assert.Equal(new[] { "blocked" }, playTime.Stopped);
                Assert.Empty(service.GetGameStates());
                Assert.Empty(service.GetRunningSessionStarts());
                store.Verify(s => s.Clear(), Times.Once);
            }
            finally
            {
                release.Set();
                service.Stop();
                DeleteDirectoryIfExists(Path.GetDirectoryName(exePath));
            }
        }

        [Theory]
        [InlineData("process-start")]
        [InlineData("process-stop")]
        [InlineData("suspend")]
        [InlineData("resume")]
        public void Stop_UnsubscribeFailure_DoesNotInterruptRemainingCleanup(string failingEvent)
        {
            var monitor = new Mock<IProcessMonitor>();
            var power = new Mock<ISystemPowerEvents>();
            var playTime = new FakePlayTime();
            var store = new Mock<IInFlightSessionStore>();
            var reminder = new Mock<IBreakReminderService>();
            var speed = new FakeSpeedController();
            var service = new GameAutomationService(monitor.Object,
                new FakeConfig(new Dictionary<string, CoreGameConfig>
                {
                    ["game"] = new() { DataKey = "game", Executable = "game.exe", IsEnabled = true, SpeedMultiplier = 2 }
                }), new FakeHdr(), playTime, NullLogger<GameAutomationService>.Instance,
                speedController: speed, breakReminder: reminder.Object, inFlightSessionStore: store.Object,
                powerEvents: power.Object);
            service.Start();
            monitor.Raise(m => m.ProcessStarted += null, new ProcessEventInfo("game.exe", null, 17));
            // Configure throwing removers after the initial subscription.
            if (failingEvent == "process-start") monitor.SetupRemove(m => m.ProcessStarted -= It.IsAny<Action<ProcessEventInfo>>()).Throws(new InvalidOperationException());
            if (failingEvent == "process-stop") monitor.SetupRemove(m => m.ProcessStopped -= It.IsAny<Action<ProcessEventInfo>>()).Throws(new InvalidOperationException());
            if (failingEvent == "suspend") power.SetupRemove(p => p.Suspending -= It.IsAny<EventHandler>()).Throws(new InvalidOperationException());
            if (failingEvent == "resume") power.SetupRemove(p => p.Resumed -= It.IsAny<EventHandler>()).Throws(new InvalidOperationException());

            service.Stop();

            monitor.VerifyRemove(m => m.ProcessStarted -= It.IsAny<Action<ProcessEventInfo>>(), Times.Once);
            monitor.VerifyRemove(m => m.ProcessStopped -= It.IsAny<Action<ProcessEventInfo>>(), Times.Once);
            power.VerifyRemove(p => p.Suspending -= It.IsAny<EventHandler>(), Times.Once);
            power.VerifyRemove(p => p.Resumed -= It.IsAny<EventHandler>(), Times.Once);
            reminder.Verify(r => r.Stop(), Times.Once);
            store.Verify(s => s.Clear(), Times.Once);
            Assert.Equal(new[] { "game" }, playTime.Stopped);
            Assert.Empty(service.GetGameStates());
            Assert.Empty(service.GetRunningSessionStarts());
            // A throwing event remover can leave a handler subscribed; it must remain inert.
            monitor.Raise(m => m.ProcessStarted += null, new ProcessEventInfo("game.exe", null, 18));
            Assert.Single(playTime.Started);
            Assert.Single(speed.Applied);
            Assert.Empty(service.GetGameStates());
        }

        [Fact]
        public void Restart_AfterStartUnsubscribeFailure_RegistersAndEndsSessionOnce()
        {
            var monitor = new Mock<IProcessMonitor>();
            var play = new FakePlayTime();
            var service = new GameAutomationService(monitor.Object, new FakeConfig(Dict(("game.exe", true, false))),
                new FakeHdr(), play, NullLogger<GameAutomationService>.Instance);
            service.Start();
            monitor.SetupRemove(m => m.ProcessStarted -= It.IsAny<Action<ProcessEventInfo>>())
                .Throws(new InvalidOperationException("unsubscribe failed"));
            service.Stop();
            service.Start();
            try
            {
                monitor.Raise(m => m.ProcessStarted += null, new ProcessEventInfo("game.exe", null, 17));
                monitor.Raise(m => m.ProcessStopped += null, new ProcessEventInfo("game.exe", null, 17));
                Assert.Equal(1, play.StartCalls);
                Assert.Equal(1, play.StopCalls);
                Assert.Empty(service.GetGameStates());
                Assert.Empty(service.GetRunningSessionStarts());
            }
            finally { service.Stop(); }
        }

        [Fact]
        public void Restart_RetainedPowerDelegates_CannotAlterNewSuspendSpan()
        {
            var monitor = new FakeMonitor();
            var power = new Mock<ISystemPowerEvents>();
            var suspendHandlers = new List<EventHandler>();
            var resumeHandlers = new List<EventHandler>();
            power.SetupAdd(p => p.Suspending += It.IsAny<EventHandler>()).Callback<EventHandler>(suspendHandlers.Add);
            power.SetupAdd(p => p.Resumed += It.IsAny<EventHandler>()).Callback<EventHandler>(resumeHandlers.Add);
            power.SetupRemove(p => p.Suspending -= It.IsAny<EventHandler>()).Throws(new InvalidOperationException());
            power.SetupRemove(p => p.Resumed -= It.IsAny<EventHandler>()).Throws(new InvalidOperationException());
            var clock = new PowerClock(new DateTimeOffset(2026, 10, 22, 20, 0, 0, TimeSpan.Zero));
            var play = new FakePlayTime();
            var service = new GameAutomationService(monitor, new FakeConfig(Dict(("game.exe", true, false))),
                new FakeHdr(), play, NullLogger<GameAutomationService>.Instance, timeProvider: clock, powerEvents: power.Object);
            service.Start();
            service.Stop();
            service.Start();
            try
            {
                monitor.RaiseStart(new ProcessEventInfo("game.exe", null, 17));
                suspendHandlers[1](null, EventArgs.Empty);
                clock.Advance(TimeSpan.FromMinutes(30));
                suspendHandlers[0](null, EventArgs.Empty);
                resumeHandlers[0](null, EventArgs.Empty);
                Assert.Empty(play.ShiftedStarts);
                clock.Advance(TimeSpan.FromMinutes(30));
                resumeHandlers[1](null, EventArgs.Empty);
                Assert.Equal(TimeSpan.FromHours(1), Assert.Single(play.ShiftedStarts));
            }
            finally { service.Stop(); }
        }

        [Fact]
        public void Restart_BetweenSessionRegistrationAndCompanion_DiscardsOldCompanionOperation()
        {
            var monitor = new FakeMonitor();
            var launcher = new FakeLauncher();
            var controller = new Mock<IMonitorModeController>();
            controller.SetupGet(c => c.IsAvailable).Returns(true);
            Action? onApply = null;
            controller.Setup(c => c.Apply(It.IsAny<IReadOnlyList<MonitorModeWrite>>())).Callback(() => onApply?.Invoke());
            var service = new GameAutomationService(monitor,
                new FakeConfig(Dict(("game.exe", true, false)), AlwaysDaylightPresets()),
                new FakeHdr(), new FakePlayTime(), NullLogger<GameAutomationService>.Instance,
                monitorModeController: controller.Object, gameLauncher: launcher);
            service.Start();
            onApply = () =>
            {
                onApply = null;
                service.Stop();
                service.Start();
            };
            try
            {
                monitor.RaiseStart(new ProcessEventInfo("game.exe", null, 17));
                Assert.Null(onApply); // The preset update actually crossed Stop -> Start.
                Assert.Equal(0, launcher.CompanionStops);
                Assert.Empty(launcher.Companions);
                Assert.Empty(service.GetGameStates());
            }
            finally { service.Stop(); }
        }

        private static IReadOnlyDictionary<string, CoreGameConfig> Dict(params (string name, bool enabled, bool hdrEnabled)[] items)
        {
            var dict = new Dictionary<string, CoreGameConfig>(StringComparer.OrdinalIgnoreCase);
            foreach (var (name, enabled, hdrEnabled) in items)
            {
                dict[name] = new CoreGameConfig
                {
                    DataKey = name,
                    Executable = name,
                    IsEnabled = enabled,
                    HdrEnabled = hdrEnabled
                };
            }
            return dict;
        }

        [Fact]
        public void ConfiguredSpeedHotkeys_AreForwardedWithInitialSpeed()
        {
            var monitor = new FakeMonitor();
            var config = new CoreGameConfig
            {
                DataKey = "game", Executable = "game.exe", SpeedMultiplier = 2,
                SpeedHotkeys = [new() { Hotkey = "Ctrl+F1", Speed = 1 }, new() { Hotkey = "F2" }]
            };
            var speed = new FakeSpeedController();
            var service = new GameAutomationService(monitor,
                new FakeConfig(new Dictionary<string, CoreGameConfig> { ["game"] = config }),
                new FakeHdr(), new FakePlayTime(), NullLogger<GameAutomationService>.Instance, speedController: speed);
            service.Start();
            monitor.RaiseStart(new ProcessEventInfo("game.exe", null, 1234));
            Assert.Equal((1234, 2d), Assert.Single(speed.Applied));
            Assert.Equal(config.SpeedHotkeys, speed.Hotkeys);
            service.Stop();
        }

        [Fact]
        public void DefaultSpeedHotkeys_ApplyOnlyToGamesWithoutOwnHotkeys()
        {
            var monitor = new FakeMonitor();
            var own = new CoreGameConfig
            {
                DataKey = "own", Executable = "own.exe", SpeedMultiplier = 2,
                SpeedHotkeys = [new() { Hotkey = "F4", Speed = 3 }]
            };
            var inherits = new CoreGameConfig { DataKey = "inherits", Executable = "inherits.exe", SpeedMultiplier = 2 };
            var configuration = new FakeConfig(new Dictionary<string, CoreGameConfig> { ["own"] = own, ["inherits"] = inherits });
            List<SpeedHotkey> defaults = [new() { Hotkey = "Ctrl+F1", Speed = 1 }, new() { Hotkey = "Ctrl+F2" }];
            configuration.Change(config => config.CheatEngine = new() { SpeedHotkeys = defaults });
            var speed = new FakeSpeedController();
            var service = new GameAutomationService(monitor, configuration,
                new FakeHdr(), new FakePlayTime(), NullLogger<GameAutomationService>.Instance, speedController: speed);
            service.Start();
            monitor.RaiseStart(new ProcessEventInfo("inherits.exe", null, 1));
            Assert.Equal(defaults, speed.Hotkeys);
            monitor.RaiseStart(new ProcessEventInfo("own.exe", null, 2));
            Assert.Equal(own.SpeedHotkeys, speed.Hotkeys);
            service.Stop();
        }

        [Fact]
        public void SingleGame_Lifecycle_EnablesAndDisablesHdr_TracksPlaytime()
        {
            var monitor = new FakeMonitor();
            var cfg = new FakeConfig(Dict(("witcher3.exe", true, true)));
            var hdr = new FakeHdr();
            var play = new FakePlayTime();
            var logger = NullLogger<GameAutomationService>.Instance;
            var svc = new GameAutomationService(monitor, cfg, hdr, play, logger, gameLauncher: new FakeLauncher());

            svc.Start();
            svc.Launch("witcher3.exe");

            // Case-insensitive start (own launch observed)
            monitor.RaiseStart(new ProcessEventInfo("WITCHER3.EXE", null));
            Assert.Equal(1, hdr.EnableCalls);
            Assert.True(hdr.IsEnabled);
            Assert.Equal(1, play.StartCalls);
            Assert.Contains(play.Started, s => string.Equals(s, "witcher3.exe", StringComparison.OrdinalIgnoreCase));

            // Stop -> disable when last game exits
            monitor.RaiseStop(new ProcessEventInfo("witcher3.exe", null));
            Assert.Equal(1, play.StopCalls);
            Assert.Equal(1, hdr.DisableCalls);
            Assert.False(hdr.IsEnabled);
        }

        [Fact]
        public void ConfiguredSpeed_AppliesOnceForFirstMatchedProcess()
        {
            var monitor = new FakeMonitor();
            var config = new CoreGameConfig
            {
                DataKey = "game",
                Executable = "game.exe",
                IsEnabled = true,
                SpeedMultiplier = 2.5
            };
            var speed = new FakeSpeedController();
            var service = new GameAutomationService(
                monitor,
                new FakeConfig(new Dictionary<string, CoreGameConfig> { ["game"] = config }),
                new FakeHdr(),
                new FakePlayTime(),
                NullLogger<GameAutomationService>.Instance,
                speedController: speed,
                gameLauncher: new FakeLauncher());

            service.Start();
            service.Launch("game");
            monitor.RaiseStart(new ProcessEventInfo("game.exe", null, 1234));
            monitor.RaiseStart(new ProcessEventInfo("game.exe", null, 5678));

            var applied = Assert.Single(speed.Applied);
            Assert.Equal(1234, applied.ProcessId);
            Assert.Equal(2.5, applied.Speed);
        }

        [Fact]
        public void EnumeratedPreexistingStart_KeepsAutomationForContinuity()
        {
            var monitor = new FakeMonitor();
            var config = new CoreGameConfig
            {
                DataKey = "game",
                Executable = "game.exe",
                IsEnabled = true,
                SpeedMultiplier = 1.5
            };
            var speed = new FakeSpeedController();
            var play = new FakePlayTime();
            var service = new GameAutomationService(
                monitor,
                new FakeConfig(new Dictionary<string, CoreGameConfig> { ["game"] = config }),
                new FakeHdr(),
                play,
                NullLogger<GameAutomationService>.Instance,
                speedController: speed);

            service.Start();

            // 枚举既有进程照常补挂变速，保持外部来源。
            monitor.RaiseStart(new ProcessEventInfo("game.exe", null, 200, Preexisting: true));
            var applied = Assert.Single(speed.Applied);
            Assert.Equal(200, applied.ProcessId);
            Assert.Equal(1.5, applied.Speed);
            Assert.Equal(1, play.StartCalls);

            // 该进程退出照常落库。
            monitor.RaiseStop(new ProcessEventInfo("game.exe", null, 200));
            Assert.Equal(1, play.StopCalls);
        }

        [Theory]
        [InlineData(false, false)]
        [InlineData(true, false)]
        [InlineData(false, true)]
        public void ExternalStart_AppliesSpeedAndCompanionWithoutHdrOrRelaunch(bool preexisting, bool companionFails)
        {
            var monitor = new FakeMonitor();
            var config = new CoreGameConfig
            {
                DataKey = "game.exe", Executable = "game.exe", IsEnabled = true,
                HdrEnabled = true, SpeedMultiplier = 2.0,
                Launch = new() { Target = @"C:\MissingGame.exe", Ds4WindowsPath = @"C:\DS4Windows.exe" }
            };
            var hdr = new FakeHdr();
            var speed = new FakeSpeedController();
            var launcher = new FakeLauncher();
            if (companionFails) launcher.OnCompanion = () => throw new IOException("companion failed");
            var play = new FakePlayTime();
            var service = new GameAutomationService(monitor,
                new FakeConfig(new Dictionary<string, CoreGameConfig> { ["game.exe"] = config }),
                hdr, play, NullLogger<GameAutomationService>.Instance, speedController: speed, gameLauncher: launcher);
            service.Start();
            monitor.RaiseStart(new ProcessEventInfo("game.exe", null, 1234, Preexisting: preexisting));
            monitor.RaiseStart(new ProcessEventInfo("game.exe", null, 1235, Preexisting: preexisting));
            Assert.Equal("运行中·外部", service.GetGameStates()["game.exe"]);
            Assert.Equal(0, hdr.EnableCalls);
            Assert.Equal(1234, Assert.Single(speed.Applied).ProcessId);
            Assert.Equal(config.Launch.Ds4WindowsPath, Assert.Single(launcher.Companions));
            Assert.Equal(1, play.StartCalls);
            Assert.Throws<InvalidOperationException>(() => service.Launch("game.exe"));
            Assert.Equal(0, launcher.Starts);
            Assert.Equal(0, hdr.EnableCalls);
            monitor.RaiseStop(new ProcessEventInfo("game.exe", null, 1234));
            monitor.RaiseStop(new ProcessEventInfo("game.exe", null, 1235));
            Assert.Equal(1, play.StopCalls);
            Assert.Equal(0, hdr.DisableCalls);
            service.Stop();
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void ExternalStart_WithControllerDisabledStopsCompanionAndKeepsTracking(bool stopFails)
        {
            var monitor = new FakeMonitor();
            var launcher = new FakeLauncher();
            if (stopFails) launcher.OnCompanion = () => throw new IOException("stop failed");
            var play = new FakePlayTime();
            var service = new GameAutomationService(monitor, new FakeConfig(Dict(("a.exe", true, false))),
                new FakeHdr(), play, NullLogger<GameAutomationService>.Instance, gameLauncher: launcher);
            service.Start();
            monitor.RaiseStart(new ProcessEventInfo("a.exe", null, 1234));
            monitor.RaiseStart(new ProcessEventInfo("a.exe", null, 1235));
            Assert.Equal(1, launcher.CompanionStops);
            Assert.Empty(launcher.Companions);
            Assert.Equal(1, play.StartCalls);
            Assert.Equal(0, launcher.Starts);
            service.Stop();
        }

        [Fact]
        public void MultipleGames_HdrOnlyOnFirstAndLast()
        {
            var monitor = new FakeMonitor();
            var cfg = new FakeConfig(Dict(("a.exe", true, true), ("b.exe", true, true)));
            var hdr = new FakeHdr();
            var play = new FakePlayTime();
            var logger = NullLogger<GameAutomationService>.Instance;
            var svc = new GameAutomationService(monitor, cfg, hdr, play, logger, gameLauncher: new FakeLauncher());

            svc.Start();
            svc.Launch("a.exe");
            svc.Launch("b.exe");

            monitor.RaiseStart(new ProcessEventInfo("a.exe", null));
            Assert.Equal(1, hdr.EnableCalls);
            Assert.True(hdr.IsEnabled);

            monitor.RaiseStart(new ProcessEventInfo("b.exe", null));
            // still only once
            Assert.Equal(1, hdr.EnableCalls);
            Assert.True(hdr.IsEnabled);

            monitor.RaiseStop(new ProcessEventInfo("a.exe", null));
            // still enabled due to b.exe
            Assert.Equal(0, hdr.DisableCalls);
            Assert.True(hdr.IsEnabled);

            monitor.RaiseStop(new ProcessEventInfo("b.exe", null));
            // last one -> disable
            Assert.Equal(1, hdr.DisableCalls);
            Assert.False(hdr.IsEnabled);
        }

        [Fact]
        public void GameConfiguredWithoutHdr_DoesNotDisablePreExistingHdr()
        {
            var monitor = new FakeMonitor();
            var cfg = new FakeConfig(Dict(("sdr.exe", true, false)));
            var hdr = new FakeHdr();
            hdr.SetState(true); // simulate HDR already enabled before the game starts
            var play = new FakePlayTime();
            var logger = NullLogger<GameAutomationService>.Instance;
            var svc = new GameAutomationService(monitor, cfg, hdr, play, logger, gameLauncher: new FakeLauncher());

            svc.Start();
            svc.Launch("sdr.exe");

            monitor.RaiseStart(new ProcessEventInfo("sdr.exe", null));

            Assert.Equal(0, hdr.DisableCalls);
            Assert.True(hdr.IsEnabled);

            monitor.RaiseStop(new ProcessEventInfo("sdr.exe", null));
            Assert.Equal(0, hdr.DisableCalls);
            Assert.True(hdr.IsEnabled);
        }

        [Fact]
        public void HdrEnabledGame_DoesNotDisableHdrThatWasAlreadyEnabledByUser()
        {
            var monitor = new FakeMonitor();
            var cfg = new FakeConfig(Dict(("hdr.exe", true, true)));
            var hdr = new FakeHdr();
            hdr.SetState(true); // user already enabled HDR before GameHelper observed the game
            var play = new FakePlayTime();
            var logger = NullLogger<GameAutomationService>.Instance;
            var svc = new GameAutomationService(monitor, cfg, hdr, play, logger, gameLauncher: new FakeLauncher());

            svc.Start();
            svc.Launch("hdr.exe");

            monitor.RaiseStart(new ProcessEventInfo("hdr.exe", null));
            Assert.Equal(0, hdr.EnableCalls);
            Assert.True(hdr.IsEnabled);

            monitor.RaiseStop(new ProcessEventInfo("hdr.exe", null));
            Assert.Equal(0, hdr.DisableCalls);
            Assert.True(hdr.IsEnabled);
        }

        [Fact]
        public void MixedHdrPreferences_OnlyEnablesWhenRequested()
        {
            var monitor = new FakeMonitor();
            var cfg = new FakeConfig(Dict(("hdr.exe", true, true), ("sdr.exe", true, false)));
            var hdr = new FakeHdr();
            var play = new FakePlayTime();
            var logger = NullLogger<GameAutomationService>.Instance;
            var svc = new GameAutomationService(monitor, cfg, hdr, play, logger, gameLauncher: new FakeLauncher());

            svc.Start();

            svc.Launch("sdr.exe");
            monitor.RaiseStart(new ProcessEventInfo("sdr.exe", null));
            Assert.Equal(0, hdr.EnableCalls);
            Assert.False(hdr.IsEnabled);

            // 库内启动在拉起进程前就确认 HDR（PrepareForLaunch 会先开启）。
            svc.Launch("hdr.exe");
            Assert.Equal(1, hdr.EnableCalls);
            Assert.True(hdr.IsEnabled);
            monitor.RaiseStart(new ProcessEventInfo("hdr.exe", null));
            Assert.Equal(1, hdr.EnableCalls);
            Assert.True(hdr.IsEnabled);

            monitor.RaiseStop(new ProcessEventInfo("hdr.exe", null));
            Assert.Equal(1, hdr.DisableCalls);
            Assert.False(hdr.IsEnabled);

            monitor.RaiseStop(new ProcessEventInfo("sdr.exe", null));
            Assert.Equal(1, hdr.DisableCalls);
        }

        [Fact]
        public void DisabledGame_Ignored()
        {
            var monitor = new FakeMonitor();
            var cfg = new FakeConfig(Dict(("c.exe", false, true)));
            var hdr = new FakeHdr();
            var play = new FakePlayTime();
            var logger = NullLogger<GameAutomationService>.Instance;
            var svc = new GameAutomationService(monitor, cfg, hdr, play, logger);

            svc.Start();

            monitor.RaiseStart(new ProcessEventInfo("c.exe", null));
            monitor.RaiseStop(new ProcessEventInfo("c.exe", null));

            Assert.Equal(0, hdr.EnableCalls);
            Assert.Equal(0, hdr.DisableCalls);
            Assert.Equal(0, play.StartCalls);
            Assert.Equal(0, play.StopCalls);
        }

        [Fact]
        public void Stop_Unsubscribes_EventsNoLongerProcessed()
        {
            var monitor = new FakeMonitor();
            var cfg = new FakeConfig(Dict(("a.exe", true, true)));
            var hdr = new FakeHdr();
            var play = new FakePlayTime();
            var logger = NullLogger<GameAutomationService>.Instance;
            var svc = new GameAutomationService(monitor, cfg, hdr, play, logger, gameLauncher: new FakeLauncher());

            svc.Start();
            svc.Launch("a.exe");
            monitor.RaiseStart(new ProcessEventInfo("a.exe", null));
            Assert.Equal(1, hdr.EnableCalls);

            svc.Stop();
            var disableCallsAfterStop = hdr.DisableCalls;

            // After Stop, further events should not be handled
            monitor.RaiseStop(new ProcessEventInfo("a.exe", null));
            monitor.RaiseStart(new ProcessEventInfo("a.exe", null));

            Assert.Equal(1, hdr.EnableCalls);
            Assert.Equal(disableCallsAfterStop, hdr.DisableCalls); // later events cannot change HDR
        }

        [Fact]
        public void Stop_FlushesEachActiveDataKeyOnce()
        {
            var monitor = new FakeMonitor();
            var cfg = new FakeConfig(Dict(("a.exe", true, true), ("b.exe", true, false)));
            var hdr = new FakeHdr();
            var play = new FakePlayTime();
            var logger = NullLogger<GameAutomationService>.Instance;
            var svc = new GameAutomationService(monitor, cfg, hdr, play, logger);

            svc.Start();
            monitor.RaiseStart(new ProcessEventInfo("a.exe", null));
            monitor.RaiseStart(new ProcessEventInfo("a.exe", null));
            monitor.RaiseStart(new ProcessEventInfo("b.exe", null));

            svc.Stop();

            Assert.Equal(2, play.StopCalls);
            Assert.Contains("a.exe", play.Stopped);
            Assert.Contains("b.exe", play.Stopped);
        }

        [Fact]
        public void StopEvent_LogsFormattedDurationIncludingSeconds()
        {
            var monitor = new FakeMonitor();
            var cfg = new FakeConfig(Dict(("game.exe", true, true)));
            var hdr = new FakeHdr();
            var play = new FakePlayTimeWithDuration(
                DateTime.SpecifyKind(new DateTime(2025, 1, 1, 8, 0, 0), DateTimeKind.Local),
                TimeSpan.FromSeconds(75));
            var logger = new ListLogger<GameAutomationService>();
            var svc = new GameAutomationService(monitor, cfg, hdr, play, logger);

            svc.Start();
            monitor.RaiseStart(new ProcessEventInfo("game.exe", null));
            monitor.RaiseStop(new ProcessEventInfo("game.exe", null));

            var entry = Assert.Single(logger.Entries, e => e.Level == LogLevel.Information && e.Message.Contains("Session duration"));
            Assert.Contains("15秒", entry.Message);
        }

        [Fact]
        public void UnmatchedStopEvent_LogsDebugInsteadOfWarning()
        {
            var monitor = new FakeMonitor();
            var cfg = new FakeConfig(Dict(("game.exe", true, false)));
            var hdr = new FakeHdr();
            var play = new FakePlayTime();
            var logger = new ListLogger<GameAutomationService>();
            var svc = new GameAutomationService(monitor, cfg, hdr, play, logger);

            svc.Start();
            monitor.RaiseStop(new ProcessEventInfo("chrome.exe", "chrome.exe"));

            Assert.Contains(
                logger.Entries,
                e => e.Level == LogLevel.Debug &&
                     e.Message.Contains("Stop ignored, no active record", StringComparison.Ordinal));
            Assert.DoesNotContain(
                logger.Entries,
                e => e.Level == LogLevel.Warning &&
                     e.Message.Contains("Stop ignored, no active record", StringComparison.Ordinal));
        }

        [Fact]
        public void ReloadConfig_ShouldRefreshPathAndNameIndexes_ForNewProcesses()
        {
            var monitor = new FakeMonitor();
            var cfg = new MutableFakeConfig(Dict(("old.exe", true, true)));
            var hdr = new FakeHdr();
            var play = new FakePlayTime();
            var logger = NullLogger<GameAutomationService>.Instance;
            var svc = new GameAutomationService(monitor, cfg, hdr, play, logger);

            svc.Start();
            monitor.RaiseStart(new ProcessEventInfo("new.exe", @"C:\Games\new.exe"));
            Assert.Equal(0, play.StartCalls);

            cfg.Set(new Dictionary<string, CoreGameConfig>(StringComparer.OrdinalIgnoreCase)
            {
                ["new.exe"] = new()
                {
                    DataKey = "new-key",
                    Executable = @"C:\Games\new.exe",
                    IsEnabled = true,
                    HdrEnabled = true
                }
            });

            svc.ReloadConfig();

            monitor.RaiseStart(new ProcessEventInfo("new.exe", @"C:\Games\new.exe"));
            Assert.Equal(1, play.StartCalls);
            Assert.Contains("new-key", play.Started, StringComparer.OrdinalIgnoreCase);
        }

        [Fact]
        public void ReloadConfig_ShouldNotBreakActiveTrackingState()
        {
            var monitor = new FakeMonitor();
            var cfg = new MutableFakeConfig(Dict(("game.exe", true, true)));
            var hdr = new FakeHdr();
            var play = new FakePlayTime();
            var logger = NullLogger<GameAutomationService>.Instance;
            var svc = new GameAutomationService(monitor, cfg, hdr, play, logger);

            svc.Start();
            monitor.RaiseStart(new ProcessEventInfo("game.exe", null));
            Assert.Equal(1, play.StartCalls);

            svc.ReloadConfig();
            monitor.RaiseStop(new ProcessEventInfo("game.exe", null));

            Assert.Equal(1, play.StopCalls);
        }

        [Fact]
        public void ReloadConfig_ShouldKeepActiveNameInProcessFilter_UntilStop()
        {
            var monitor = new FakeMonitor();
            var cfg = new MutableFakeConfig(Dict(("game.exe", true, true)));
            var hdr = new FakeHdr();
            var play = new FakePlayTime();
            var logger = NullLogger<GameAutomationService>.Instance;
            var svc = new GameAutomationService(monitor, cfg, hdr, play, logger);

            svc.Start();
            monitor.RaiseStart(new ProcessEventInfo("game.exe", null));

            cfg.Set(new Dictionary<string, CoreGameConfig>(StringComparer.OrdinalIgnoreCase));
            svc.ReloadConfig();

            Assert.Contains("game.exe", monitor.LastPolicy.CandidateProcessNames, StringComparer.OrdinalIgnoreCase);
            Assert.True(monitor.LastPolicy.ObserveStopEvents);

            monitor.RaiseStop(new ProcessEventInfo("game.exe", null));

            Assert.Equal(1, play.StopCalls);
            Assert.DoesNotContain("game.exe", monitor.LastPolicy.CandidateProcessNames, StringComparer.OrdinalIgnoreCase);
            Assert.False(monitor.LastPolicy.ObserveStopEvents);
        }

        [Fact]
        public void Start_DuplicateExecutableNameWithMissingPath_LogsWarning()
        {
            var monitor = new FakeMonitor();
            var cfg = new FakeConfig(new Dictionary<string, CoreGameConfig>(StringComparer.OrdinalIgnoreCase)
            {
                ["entry1"] = new()
                {
                    DataKey = "game1",
                    Executable = @"D:\Games\A\Game.exe",
                    IsEnabled = true
                },
                ["entry2"] = new()
                {
                    DataKey = "game2",
                    Executable = "Game.exe",
                    IsEnabled = true
                }
            });
            var hdr = new FakeHdr();
            var play = new FakePlayTime();
            var logger = new ListLogger<GameAutomationService>();
            var svc = new GameAutomationService(monitor, cfg, hdr, play, logger);

            svc.Start();

            Assert.Contains(
                logger.Entries,
                e => e.Level == LogLevel.Warning &&
                     e.Message.Contains("Duplicate executable name detected while building indexes", StringComparison.Ordinal));
        }

        [Fact]
        public void Start_DuplicateExecutableNameWithAllPaths_OnlyLogsDebug()
        {
            var monitor = new FakeMonitor();
            var cfg = new FakeConfig(new Dictionary<string, CoreGameConfig>(StringComparer.OrdinalIgnoreCase)
            {
                ["entry1"] = new()
                {
                    DataKey = "game1",
                    Executable = @"D:\Games\A\Game.exe",
                    IsEnabled = true
                },
                ["entry2"] = new()
                {
                    DataKey = "game2",
                    Executable = @"D:\Games\B\Game.exe",
                    IsEnabled = true
                }
            });
            var hdr = new FakeHdr();
            var play = new FakePlayTime();
            var logger = new ListLogger<GameAutomationService>();
            var svc = new GameAutomationService(monitor, cfg, hdr, play, logger);

            svc.Start();

            Assert.DoesNotContain(
                logger.Entries,
                e => e.Level == LogLevel.Warning &&
                     e.Message.Contains("Duplicate executable name detected while building indexes", StringComparison.Ordinal));
            Assert.Contains(
                logger.Entries,
                e => e.Level == LogLevel.Debug &&
                     e.Message.Contains("Duplicate executable name detected while building indexes", StringComparison.Ordinal));
        }

        // ---- speed attach gating (mod-loader aware) ----

        private static readonly TimeSpan TinyPollInterval = TimeSpan.FromMilliseconds(25);

        private static string CreateModdedGameDirectory(string exeFileName)
        {
            var dir = Path.Combine(Path.GetTempPath(), "GameHelperTests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(dir, "BepInEx"));
            var exePath = Path.Combine(dir, exeFileName);
            File.WriteAllBytes(exePath, Array.Empty<byte>());
            File.WriteAllText(Path.Combine(dir, "BepInEx", "LogOutput.log"), "[Info   : Preloader] booting\n");
            return exePath;
        }

        private static void DeleteDirectoryIfExists(string? path)
        {
            try
            {
                if (path is not null && Directory.Exists(path)) Directory.Delete(path, recursive: true);
            }
            catch
            {
                // Best-effort cleanup; nested deletes fail transiently on Windows.
            }
        }

        private static void AppendChainloaderMarker(string exePath) =>
            File.AppendAllText(
                Path.Combine(Path.GetDirectoryName(exePath)!, "BepInEx", "LogOutput.log"),
                "[Message:   BepInEx] Chainloader startup complete\n");

        [Fact]
        public async Task ConfiguredSpeed_WithBepInExDetected_DefersAttachUntilChainloaderMarker()
        {
            var exePath = CreateModdedGameDirectory("GameX.exe");
            try
            {
                var monitor = new FakeMonitor();
                var config = new CoreGameConfig
                {
                    DataKey = "gamex",
                    Executable = exePath,
                    IsEnabled = true,
                    SpeedMultiplier = 1.5,
                    SpeedHotkeys = [new() { Hotkey = "Ctrl+F1", Speed = 1 }]
                };
                var source = new FakeConfig(new Dictionary<string, CoreGameConfig> { ["gamex"] = config });
                var speed = new FakeSpeedController();
                var service = new GameAutomationService(
                    monitor,
                    source,
                    new FakeHdr(),
                    new FakePlayTime(),
                    NullLogger<GameAutomationService>.Instance,
                    speedController: speed,
                    speedGatePollInterval: TinyPollInterval,
                    speedGateTimeout: TimeSpan.FromSeconds(30),
                    gameLauncher: new FakeLauncher());

                service.Start();
                service.Launch("gamex");
                monitor.RaiseStart(new ProcessEventInfo("GameX.exe", exePath, 4321));

                // The chainloader marker was not written yet: the attach must stay deferred.
                await Task.Delay(200);
                Assert.Empty(speed.Applied);

                // Reload while gated: this activation must retain its original speed/key snapshot.
                source.Change(document =>
                {
                    document.Games[0].SpeedMultiplier = 4;
                    document.Games[0].SpeedHotkeys = [new() { Hotkey = "F4", Speed = 3 }];
                });
                service.ReloadConfig();
                AppendChainloaderMarker(exePath);

                await speed.WaitForFirstApplyAsync(TimeSpan.FromSeconds(5));
                var applied = Assert.Single(speed.Applied);
                Assert.Equal(4321, applied.ProcessId);
                Assert.Equal(1.5, applied.Speed);
                Assert.Equal(config.SpeedHotkeys, speed.Hotkeys);
                service.Stop();
            }
            finally
            {
                DeleteDirectoryIfExists(Path.GetDirectoryName(exePath));
            }
        }

        [Fact]
        public async Task ConfiguredSpeed_WithBepInExDetected_AttachesOnTimeoutFallback()
        {
            var exePath = CreateModdedGameDirectory("GameY.exe");
            try
            {
                var monitor = new FakeMonitor();
                var config = new CoreGameConfig
                {
                    DataKey = "gamey",
                    Executable = exePath,
                    IsEnabled = true,
                    SpeedMultiplier = 1.5
                };
                var speed = new FakeSpeedController();
                var service = new GameAutomationService(
                    monitor,
                    new FakeConfig(new Dictionary<string, CoreGameConfig> { ["gamey"] = config }),
                    new FakeHdr(),
                    new FakePlayTime(),
                    NullLogger<GameAutomationService>.Instance,
                    speedController: speed,
                    speedGatePollInterval: TinyPollInterval,
                    speedGateTimeout: TimeSpan.FromMilliseconds(300),
                    gameLauncher: new FakeLauncher());

                service.Start();
                service.Launch("gamey");
                monitor.RaiseStart(new ProcessEventInfo("GameY.exe", exePath, 4321));

                // No marker is ever written; the timeout fallback must attach anyway.
                await speed.WaitForFirstApplyAsync(TimeSpan.FromSeconds(5));
                var applied = Assert.Single(speed.Applied);
                Assert.Equal(4321, applied.ProcessId);
            }
            finally
            {
                DeleteDirectoryIfExists(Path.GetDirectoryName(exePath));
            }
        }

        [Fact]
        public async Task ConfiguredSpeed_ImmediatePolicy_BypassesModLoaderGate()
        {
            var exePath = CreateModdedGameDirectory("GameZ.exe");
            try
            {
                var monitor = new FakeMonitor();
                var config = new CoreGameConfig
                {
                    DataKey = "gamez",
                    Executable = exePath,
                    IsEnabled = true,
                    SpeedMultiplier = 1.5,
                    SpeedAttach = CoreGameConfig.SpeedAttachPolicyImmediate
                };
                var speed = new FakeSpeedController();
                var service = new GameAutomationService(
                    monitor,
                    new FakeConfig(new Dictionary<string, CoreGameConfig> { ["gamez"] = config }),
                    new FakeHdr(),
                    new FakePlayTime(),
                    NullLogger<GameAutomationService>.Instance,
                    speedController: speed,
                    speedGatePollInterval: TinyPollInterval,
                    speedGateTimeout: TimeSpan.FromSeconds(30),
                    gameLauncher: new FakeLauncher());

                service.Start();
                service.Launch("gamez");
                monitor.RaiseStart(new ProcessEventInfo("GameZ.exe", exePath, 4321));

                // No chainloader marker is written; an immediate attach must not wait for it.
                await speed.WaitForFirstApplyAsync(TimeSpan.FromSeconds(3));
                var applied = Assert.Single(speed.Applied);
                Assert.Equal(4321, applied.ProcessId);
            }
            finally
            {
                DeleteDirectoryIfExists(Path.GetDirectoryName(exePath));
            }
        }

        [Fact]
        public async Task StopEvent_CancelsPendingDeferredAttach()
        {
            var exePath = CreateModdedGameDirectory("GameStopped.exe");
            try
            {
                var monitor = new FakeMonitor();
                var config = new CoreGameConfig
                {
                    DataKey = "gamestopped",
                    Executable = exePath,
                    IsEnabled = true,
                    SpeedMultiplier = 1.5
                };
                var speed = new FakeSpeedController();
                var service = new GameAutomationService(
                    monitor,
                    new FakeConfig(new Dictionary<string, CoreGameConfig> { ["gamestopped"] = config }),
                    new FakeHdr(),
                    new FakePlayTime(),
                    NullLogger<GameAutomationService>.Instance,
                    speedController: speed,
                    speedGatePollInterval: TinyPollInterval,
                    speedGateTimeout: TimeSpan.FromMilliseconds(500),
                    gameLauncher: new FakeLauncher());

                service.Start();
                service.Launch("gamestopped");
                monitor.RaiseStart(new ProcessEventInfo("GameStopped.exe", exePath, 4321));
                await Task.Delay(100);
                monitor.RaiseStop(new ProcessEventInfo("GameStopped.exe", exePath, 4321));

                // The deferred gate must not attach after the game stopped (timeout would be 500 ms).
                await Task.Delay(1500);
                Assert.Empty(speed.Applied);
            }
            finally
            {
                DeleteDirectoryIfExists(Path.GetDirectoryName(exePath));
            }
        }

        [Fact]
        public async Task GameExit_ShortlyAfterCheatEngineAttach_RecordsCorrelationLog()
        {
            var monitor = new FakeMonitor();
            var config = new CoreGameConfig
            {
                DataKey = "gamecorr",
                Executable = "corr-game.exe",
                IsEnabled = true,
                SpeedMultiplier = 1.5
            };
            var speed = new FakeSpeedController();
            var logger = new ListLogger<GameAutomationService>();
            var service = new GameAutomationService(
                monitor,
                new FakeConfig(new Dictionary<string, CoreGameConfig> { ["gamecorr"] = config }),
                new FakeHdr(),
                new FakePlayTime(),
                logger,
                speedController: speed,
                gameLauncher: new FakeLauncher());

            service.Start();
            service.Launch("gamecorr");
            monitor.RaiseStart(new ProcessEventInfo("corr-game.exe", null, 999));
            monitor.RaiseStop(new ProcessEventInfo("corr-game.exe", null, 999));

            // The immediate attach records the correlation candidate; the stop logs the association.
            await Task.Delay(300);
            Assert.Contains(
                logger.Entries,
                e => e.Message.Contains("attached Cheat Engine", StringComparison.Ordinal));
        }

        [Fact]
        public async Task GameExit_WithoutSpeedConfig_DoesNotLogCorrelation()
        {
            var monitor = new FakeMonitor();
            var config = new CoreGameConfig
            {
                DataKey = "gameplain",
                Executable = "plain-game.exe",
                IsEnabled = true
            };
            var logger = new ListLogger<GameAutomationService>();
            var service = new GameAutomationService(
                monitor,
                new FakeConfig(new Dictionary<string, CoreGameConfig> { ["gameplain"] = config }),
                new FakeHdr(),
                new FakePlayTime(),
                logger);

            service.Start();
            monitor.RaiseStart(new ProcessEventInfo("plain-game.exe", null, 1001));
            monitor.RaiseStop(new ProcessEventInfo("plain-game.exe", null, 1001));

            await Task.Delay(300);
            Assert.DoesNotContain(
                logger.Entries,
                e => e.Message.Contains("attached Cheat Engine", StringComparison.Ordinal));
        }

        [Fact]
        public void SystemSuspendResume_ShiftsRunningSessionStartsAndExcludesPausedSpan()
        {
            var monitor = new FakeMonitor();
            var cfg = new FakeConfig(Dict(("game.exe", true, false)));
            var hdr = new FakeHdr();
            var play = new FakePlayTime();
            var power = new FakePowerEvents();
            var clock = new PowerClock(new DateTimeOffset(2026, 10, 22, 20, 0, 0, TimeSpan.Zero));
            var service = new GameAutomationService(
                monitor, cfg, hdr, play, NullLogger<GameAutomationService>.Instance,
                timeProvider: clock, powerEvents: power);
            service.Start();

            // 未休眠却收到唤醒（或事件迟到）时不凭空调整。
            power.RaiseResume();
            Assert.Empty(play.ShiftedStarts);

            monitor.RaiseStart(new ProcessEventInfo("game.exe", null, 7));
            var startedAt = service.GetRunningSessionStarts()["game.exe"];
            Assert.NotNull(startedAt);

            power.RaiseSuspend();
            clock.Advance(TimeSpan.FromHours(2));
            power.RaiseResume();

            Assert.Equal(TimeSpan.FromHours(2), Assert.Single(play.ShiftedStarts));
            Assert.NotNull(startedAt);
            Assert.Equal(TimeSpan.FromHours(2), service.GetRunningSessionStarts()["game.exe"]!.Value - startedAt!.Value);

            // 结束走正常会话流程；计时视图不再包含运行中的条目。
            monitor.RaiseStop(new ProcessEventInfo("game.exe", null, 7));
            Assert.Empty(service.GetRunningSessionStarts());
        }

        [Fact]
        public void SystemResumeWithoutSuspendOrIdleMachine_DoesNotShift()
        {
            var monitor = new FakeMonitor();
            var cfg = new FakeConfig(Dict(("game.exe", true, false)));
            var hdr = new FakeHdr();
            var play = new FakePlayTime();
            var power = new FakePowerEvents();
            var clock = new PowerClock(new DateTimeOffset(2026, 10, 22, 20, 0, 0, TimeSpan.Zero));
            var service = new GameAutomationService(
                monitor, cfg, hdr, play, NullLogger<GameAutomationService>.Instance,
                timeProvider: clock, powerEvents: power);
            service.Start();

            // 没有活跃会话时，休眠/唤醒不需要调整任何东西。
            power.RaiseSuspend();
            clock.Advance(TimeSpan.FromHours(8));
            power.RaiseResume();
            Assert.Empty(play.ShiftedStarts);

            // 会话启动后同样跳过：挂起时间戳只属于真正休眠过的那一轮。
            monitor.RaiseStart(new ProcessEventInfo("game.exe", null, 7));
            power.RaiseResume();
            Assert.Empty(play.ShiftedStarts);
        }

        [Fact]
        public void MonitorModeAutomation_PausesDuringHdrAutomationGameRun_AndResumesAfterExit()
        {
            var monitor = new FakeMonitor();
            var hdr = new FakeHdr();
            var play = new FakePlayTime();
            var clock = new PowerClock(new DateTimeOffset(2026, 10, 23, 12, 0, 0, TimeSpan.FromHours(8)));
            var presets = AlwaysDaylightPresets();
            var config = new FakeConfig(Dict(("hdrgame", true, true)), presets);
            var controller = new RecordingMonitorModeController();
            var launcher = new FakeLauncher();
            var service = new GameAutomationService(
                monitor, config, hdr, play, NullLogger<GameAutomationService>.Instance,
                monitorModeController: controller, timeProvider: clock, gameLauncher: launcher);
            service.Start();

            // 启动评估：白天无游戏 → 护眼档位应用一次。
            Assert.Single(controller.Modes);
            Assert.Equal(MonitorMode.Comfort, controller.Modes[^1]);

            // 库内启动勾选 HDR 的游戏：先确认开启 HDR，预设自动化随之暂停（期间零 DDC I/O）。
            var writesBeforeLaunch = controller.Recipes.Count;
            service.Launch("hdrgame");
            monitor.RaiseStart(new ProcessEventInfo("hdrgame.exe", null, 77));
            Assert.Equal(writesBeforeLaunch, controller.Recipes.Count);

            // 游戏退出且 HDR 恢复关闭：预设自动化恢复并按白天无游戏的口径切回护眼。
            monitor.RaiseStop(new ProcessEventInfo("hdrgame.exe", null, 77));
            Assert.Equal(writesBeforeLaunch + 1, controller.Recipes.Count);
            Assert.Equal(MonitorMode.Comfort, controller.Modes[^1]);
            Assert.False(hdr.IsEnabled);
        }

        [Fact]
        public void MonitorModeAutomation_ExternalHdrFlaggedSession_FallsBackToGamePreset()
        {
            var monitor = new FakeMonitor();
            var hdr = new FakeHdr();
            var play = new FakePlayTime();
            var clock = new PowerClock(new DateTimeOffset(2026, 10, 23, 12, 0, 0, TimeSpan.FromHours(8)));
            var presets = AlwaysDaylightPresets();
            var config = new FakeConfig(Dict(("hdrgame", true, true)), presets);
            var controller = new RecordingMonitorModeController();
            var service = new GameAutomationService(
                monitor, config, hdr, play, NullLogger<GameAutomationService>.Instance,
                monitorModeController: controller, timeProvider: clock);
            service.Start();
            Assert.Single(controller.Modes);

            // 外部启动不会切换 HDR，即使勾选 HDR 也降级为游戏预设。
            monitor.RaiseStart(new ProcessEventInfo("hdrgame.exe", null, 78));
            Assert.Equal(MonitorMode.Game, controller.Modes[^1]);
            Assert.Equal(0, hdr.EnableCalls);
            var writesWithGameActive = controller.Recipes.Count;

            monitor.RaiseStop(new ProcessEventInfo("hdrgame.exe", null, 78));
            Assert.Equal(writesWithGameActive + 1, controller.Recipes.Count);
            Assert.Equal(MonitorMode.Comfort, controller.Modes[^1]);
        }

        private static MonitorModeSettings AlwaysDaylightPresets() => new()
        {
            Enabled = true,
            DaylightStart = "00:00",
            DaylightEnd = "23:59",
            GamePresets =
            [
                new() { Code = "DC", Value = "0x05" },
                new() { Code = "E2", Value = "0x04" },
                new() { Code = "F0", Value = "0x00" }
            ],
            ComfortPresets =
            [
                new() { Code = "DC", Value = "0x00" },
                new() { Code = "E2", Value = "0x1D" },
                new() { Code = "F0", Value = "0x0C" }
            ]
        };
    }

    file sealed class RecordingMonitorModeController : IMonitorModeController
    {
        public List<IReadOnlyList<MonitorModeWrite>> Recipes { get; } = new();
        public List<MonitorMode> Modes { get; } = new();
        public int ReadCount { get; private set; }
        public bool IsAvailable => true;

        public IReadOnlyDictionary<byte, uint>? TryReadState(IReadOnlyList<MonitorModeWrite> probeCodes)
        {
            ReadCount++;
            return null;
        }

        public void Apply(IReadOnlyList<MonitorModeWrite> writes)
        {
            Recipes.Add(writes);
            var e2 = writes.FirstOrDefault(write => write.Code == 0xE2);
            Modes.Add(e2.Value == 0x04 ? MonitorMode.Game : MonitorMode.Comfort);
        }
    }
}
