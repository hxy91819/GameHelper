using GameHelper.Core.Abstractions;
using GameHelper.Core.Models;
using GameHelper.Core.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace GameHelper.Tests;

public sealed class SpeedControlServiceTests
{
    [Fact]
    public void Start_WhenHotkeyRegistrationFails_DisablesHotkey()
    {
        var service = CreateService(
            appConfig: new AppConfig(),
            configs: new Dictionary<string, GameConfig>(),
            hotkeyListener: new FakeHotkeyListener
            {
                StartResult = new HotkeyRegistrationResult
                {
                    Success = false,
                    Message = "热键 'Ctrl+Alt+F10' 注册失败，可能已被占用。"
                }
            });

        service.Start();

        var status = service.GetStatus();
        Assert.True(status.IsSupported);
        Assert.False(status.HotkeyRegistered);
        Assert.Contains("占用", status.Message);
    }

    [Fact]
    public void HotkeyTrigger_TogglesSpeedForForegroundGame()
    {
        var listener = new FakeHotkeyListener();
        var engine = new FakeSpeedEngine();
        var service = CreateService(
            appConfig: new AppConfig { DefaultSpeedMultiplier = 2.0, SpeedToggleHotkey = "Ctrl+Alt+F10" },
            configs: new Dictionary<string, GameConfig>
            {
                ["entry"] = new()
                {
                    EntryId = "entry",
                    DataKey = "celeste",
                    ExecutableName = "celeste.exe",
                    IsEnabled = true,
                    SpeedEnabled = true,
                    SpeedMultiplier = 2.5
                }
            },
            foregroundProcessResolver: new FakeForegroundProcessResolver
            {
                Current = new ForegroundProcessInfo(42, "celeste.exe", @"C:\Games\celeste.exe")
            },
            hotkeyListener: listener,
            speedEngine: engine);

        service.Start();
        listener.Trigger();

        Assert.Equal((42, 2.5), Assert.Single(engine.ApplyCalls));

        listener.Trigger();

        Assert.Equal(42, Assert.Single(engine.ResetCalls));
    }

    [Fact]
    public void HotkeyTrigger_IgnoresGameWithoutSpeedEnabled()
    {
        var listener = new FakeHotkeyListener();
        var engine = new FakeSpeedEngine();
        var service = CreateService(
            appConfig: new AppConfig(),
            configs: new Dictionary<string, GameConfig>
            {
                ["entry"] = new()
                {
                    EntryId = "entry",
                    DataKey = "demo",
                    ExecutableName = "demo.exe",
                    IsEnabled = true,
                    SpeedEnabled = false
                }
            },
            foregroundProcessResolver: new FakeForegroundProcessResolver
            {
                Current = new ForegroundProcessInfo(77, "demo.exe", @"C:\Games\demo.exe")
            },
            hotkeyListener: listener,
            speedEngine: engine);

        service.Start();
        listener.Trigger();

        Assert.Empty(engine.ApplyCalls);
        Assert.Empty(engine.ResetCalls);
    }

    private static SpeedControlService CreateService(
        AppConfig? appConfig = null,
        IReadOnlyDictionary<string, GameConfig>? configs = null,
        FakeForegroundProcessResolver? foregroundProcessResolver = null,
        FakeHotkeyListener? hotkeyListener = null,
        FakeSpeedEngine? speedEngine = null)
    {
        return new SpeedControlService(
            new FakeAppConfigProvider(appConfig ?? new AppConfig()),
            new FakeConfigProvider(configs ?? new Dictionary<string, GameConfig>()),
            foregroundProcessResolver ?? new FakeForegroundProcessResolver(),
            new GameProcessMatcher(NullLogger<GameProcessMatcher>.Instance),
            hotkeyListener ?? new FakeHotkeyListener(),
            speedEngine ?? new FakeSpeedEngine(),
            NullLogger<SpeedControlService>.Instance);
    }

    private sealed class FakeAppConfigProvider : IAppConfigProvider
    {
        private AppConfig _appConfig;

        public FakeAppConfigProvider(AppConfig appConfig)
        {
            _appConfig = appConfig;
        }

        public AppConfig LoadAppConfig() => _appConfig;

        public void SaveAppConfig(AppConfig appConfig)
        {
            _appConfig = appConfig;
        }
    }

    private sealed class FakeConfigProvider : IConfigProvider
    {
        private readonly IReadOnlyDictionary<string, GameConfig> _configs;

        public FakeConfigProvider(IReadOnlyDictionary<string, GameConfig> configs)
        {
            _configs = configs;
        }

        public IReadOnlyDictionary<string, GameConfig> Load() => _configs;

        public void Save(IReadOnlyDictionary<string, GameConfig> configs)
        {
        }
    }

    private sealed class FakeForegroundProcessResolver : IForegroundProcessResolver
    {
        public ForegroundProcessInfo? Current { get; set; }

        public ForegroundProcessInfo? GetForegroundProcess() => Current;
    }

    private sealed class FakeHotkeyListener : IGlobalHotkeyListener
    {
        private Action? _callback;

        public bool IsListening { get; private set; }

        public HotkeyRegistrationResult StartResult { get; set; } = new()
        {
            Success = true,
            Message = "热键已注册：Ctrl+Alt+F10"
        };

        public HotkeyRegistrationResult Start(HotkeyBinding binding, Action onTriggered)
        {
            _callback = onTriggered;
            IsListening = StartResult.Success;
            return StartResult;
        }

        public void Stop()
        {
            IsListening = false;
        }

        public void Trigger()
        {
            _callback?.Invoke();
        }
    }

    private sealed class FakeSpeedEngine : ISpeedEngine
    {
        public bool IsSupported { get; set; } = true;

        public string UnavailableReason { get; set; } = string.Empty;

        public List<(int ProcessId, double Multiplier)> ApplyCalls { get; } = [];

        public List<int> ResetCalls { get; } = [];

        public SpeedOperationResult ApplySpeed(int processId, double multiplier)
        {
            ApplyCalls.Add((processId, multiplier));
            return new SpeedOperationResult { Success = true, Message = "ok" };
        }

        public SpeedOperationResult ResetSpeed(int processId)
        {
            ResetCalls.Add(processId);
            return new SpeedOperationResult { Success = true, Message = "ok" };
        }

        public void Dispose()
        {
        }
    }
}
