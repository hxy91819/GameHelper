using System;
using GameHelper.Core.Abstractions;
using GameHelper.Core.Models;
using GameHelper.Core.Utilities;
using Microsoft.Extensions.Logging;

namespace GameHelper.Core.Services;

public sealed class SpeedControlService : ISpeedControlService
{
    private readonly IAppConfigProvider _appConfigProvider;
    private readonly IConfigProvider _configProvider;
    private readonly IForegroundProcessResolver _foregroundProcessResolver;
    private readonly IGameProcessMatcher _gameProcessMatcher;
    private readonly IGlobalHotkeyListener _hotkeyListener;
    private readonly ISpeedEngine _speedEngine;
    private readonly ILogger<SpeedControlService> _logger;
    private readonly object _sync = new();

    private GameProcessMatchSnapshot _snapshot = new();
    private AppConfig _appConfig = new();
    private SpeedControlStatus _status = new();
    private int? _activeProcessId;
    private string? _activeDataKey;
    private double _activeMultiplier;

    public SpeedControlService(
        IAppConfigProvider appConfigProvider,
        IConfigProvider configProvider,
        IForegroundProcessResolver foregroundProcessResolver,
        IGameProcessMatcher gameProcessMatcher,
        IGlobalHotkeyListener hotkeyListener,
        ISpeedEngine speedEngine,
        ILogger<SpeedControlService> logger)
    {
        _appConfigProvider = appConfigProvider;
        _configProvider = configProvider;
        _foregroundProcessResolver = foregroundProcessResolver;
        _gameProcessMatcher = gameProcessMatcher;
        _hotkeyListener = hotkeyListener;
        _speedEngine = speedEngine;
        _logger = logger;
        _status = new SpeedControlStatus
        {
            Hotkey = SpeedDefaults.DefaultHotkey,
            Message = "倍速服务尚未启动。"
        };
    }

    public bool IsRunning { get; private set; }

    public void Start()
    {
        lock (_sync)
        {
            if (IsRunning)
            {
                return;
            }

            _appConfig = _appConfigProvider.LoadAppConfig();
            _appConfig.DefaultSpeedMultiplier = SpeedDefaults.NormalizeMultiplier(_appConfig.DefaultSpeedMultiplier);
            _appConfig.SpeedToggleHotkey = SpeedDefaults.NormalizeHotkey(_appConfig.SpeedToggleHotkey);
            _snapshot = _gameProcessMatcher.CreateSnapshot(_configProvider.Load());

            if (!_speedEngine.IsSupported)
            {
                _status = new SpeedControlStatus
                {
                    IsSupported = false,
                    Hotkey = _appConfig.SpeedToggleHotkey,
                    Message = _speedEngine.UnavailableReason
                };
                _logger.LogWarning("倍速服务不可用：{Reason}", _speedEngine.UnavailableReason);
                IsRunning = true;
                return;
            }

            if (!HotkeyBindingParser.TryParse(_appConfig.SpeedToggleHotkey, out var binding, out var error) || binding is null)
            {
                _status = new SpeedControlStatus
                {
                    IsSupported = true,
                    Hotkey = _appConfig.SpeedToggleHotkey,
                    Message = $"热键配置无效：{error}"
                };
                _logger.LogWarning("倍速热键配置无效：{Error}", error);
                IsRunning = true;
                return;
            }

            var result = _hotkeyListener.Start(binding, OnHotkeyTriggered);
            _status = new SpeedControlStatus
            {
                IsSupported = true,
                Hotkey = binding.DisplayText,
                HotkeyRegistered = result.Success,
                Message = result.Message
            };

            if (result.Success)
            {
                _logger.LogInformation("倍速热键已注册：{Hotkey}", binding.DisplayText);
            }
            else
            {
                _logger.LogWarning("倍速热键已禁用：{Reason}", result.Message);
            }

            IsRunning = true;
        }
    }

    public void Stop()
    {
        lock (_sync)
        {
            if (!IsRunning)
            {
                return;
            }

            ResetActiveSpeedUnsafe();
            _hotkeyListener.Stop();

            _activeProcessId = null;
            _activeDataKey = null;
            _activeMultiplier = 0;
            IsRunning = false;
        }
    }

    public SpeedControlStatus GetStatus()
    {
        lock (_sync)
        {
            return new SpeedControlStatus
            {
                IsSupported = _status.IsSupported,
                Hotkey = _status.Hotkey,
                HotkeyRegistered = _status.HotkeyRegistered,
                Message = _status.Message
            };
        }
    }

    private void OnHotkeyTriggered()
    {
        lock (_sync)
        {
            if (!_status.HotkeyRegistered)
            {
                return;
            }

            var foreground = _foregroundProcessResolver.GetForegroundProcess();
            if (foreground is null)
            {
                _logger.LogInformation("倍速切换已忽略：无法获取当前前台进程。");
                return;
            }

            var match = _gameProcessMatcher.Match(new ProcessEventInfo(foreground.ExecutableName, foreground.ExecutablePath), _snapshot);
            if (match is null)
            {
                _logger.LogInformation("倍速切换已忽略：前台进程 {ExecutableName} 未匹配到已配置游戏。", foreground.ExecutableName);
                return;
            }

            if (!match.Config.SpeedEnabled)
            {
                _logger.LogInformation("倍速切换已忽略：游戏 {DataKey} 未启用倍速。", match.Config.DataKey);
                return;
            }

            var multiplier = SpeedDefaults.NormalizeMultiplier(match.Config.SpeedMultiplier ?? _appConfig.DefaultSpeedMultiplier);

            if (_activeProcessId == foreground.ProcessId)
            {
                var resetResult = _speedEngine.ResetSpeed(foreground.ProcessId);
                if (resetResult.Success)
                {
                    _logger.LogInformation("倍速已恢复为 1.0x：{DataKey} (PID={ProcessId})", match.Config.DataKey, foreground.ProcessId);
                    _activeProcessId = null;
                    _activeDataKey = null;
                    _activeMultiplier = 0;
                }
                else
                {
                    _logger.LogWarning("倍速恢复失败：{Message}", resetResult.Message);
                }

                return;
            }

            ResetActiveSpeedUnsafe();

            var applyResult = _speedEngine.ApplySpeed(foreground.ProcessId, multiplier);
            if (applyResult.Success)
            {
                _activeProcessId = foreground.ProcessId;
                _activeDataKey = match.Config.DataKey;
                _activeMultiplier = multiplier;
                _logger.LogInformation(
                    "倍速已切换：{DataKey} (PID={ProcessId}) -> {Multiplier:0.##}x",
                    match.Config.DataKey,
                    foreground.ProcessId,
                    multiplier);
            }
            else
            {
                _logger.LogWarning("倍速切换失败：{Message}", applyResult.Message);
            }
        }
    }

    private void ResetActiveSpeedUnsafe()
    {
        if (!_activeProcessId.HasValue)
        {
            return;
        }

        var result = _speedEngine.ResetSpeed(_activeProcessId.Value);
        if (result.Success)
        {
            _logger.LogInformation(
                "已清理倍速状态：{DataKey} (PID={ProcessId}) 从 {Multiplier:0.##}x 恢复为 1.0x",
                _activeDataKey ?? "unknown",
                _activeProcessId.Value,
                _activeMultiplier);
            _activeProcessId = null;
            _activeDataKey = null;
            _activeMultiplier = 0;
        }
        else
        {
            _logger.LogWarning("清理倍速状态失败：{Message}", result.Message);
        }
    }
}
