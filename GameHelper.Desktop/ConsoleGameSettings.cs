using GameHelper.Core.Abstractions;
using GameHelper.Core.Models;

namespace GameHelper.Desktop;

/// <summary>A private console settings draft over one revision of the saved catalog entry.</summary>
internal sealed class ConsoleGameSettings(IGameLibrary library, LibraryGame baseline)
{
    internal string DataKey => baseline.Config.DataKey;
    internal string Name => baseline.Config.DisplayName ?? DataKey;
    internal bool Hdr { get; set; } = baseline.Config.HdrEnabled;
    internal bool UseDs4Windows { get; set; } = baseline.Config.Launch?.Ds4WindowsPath is not null;
    internal bool UseSpeed { get; set; } = baseline.Config.SpeedMultiplier is not null;
    // Reading a historical out-of-range value does not silently rewrite it.
    internal double Speed { get; private set; } = baseline.Config.SpeedMultiplier ?? 1.5;
    internal string Ds4WindowsPath => baseline.Config.Launch?.Ds4WindowsPath ?? LibraryGameItem.DefaultDs4WindowsPath;
    internal const string SavedMessage = "已保存。DS4Windows 与倍速在下次启动游戏时应用；HDR 自动配置在后续自动调度时应用。";

    internal void AdjustSpeed(int direction)
    {
        Speed = Math.Clamp(Math.Round((Math.Clamp(Speed, 0.1, 10) + Math.Sign(direction) * 0.1) * 10,
            MidpointRounding.AwayFromZero) / 10, 0.1, 10);
    }

    internal async Task SaveAsync()
    {
        var config = baseline.Config.Copy();
        config.HdrEnabled = Hdr;
        config.SpeedMultiplier = UseSpeed ? Speed : null;
        if (UseDs4Windows != (baseline.Config.Launch?.Ds4WindowsPath is not null))
        {
            var launch = baseline.Config.Launch;
            if (UseDs4Windows)
            {
                launch ??= baseline.Config.ExecutablePath is { } path ? new GameLaunchOptions { Target = path } : null;
                if (launch is null) throw new InvalidOperationException("请先在桌面游戏库配置此游戏的启动入口，再启用 DS4Windows。");
                config.Launch = launch with { Ds4WindowsPath = Ds4WindowsPath };
            }
            else if (launch is not null) config.Launch = launch with { Ds4WindowsPath = null };
        }
        var response = await library.ExecuteAsync(new GameLibraryRequest
        {
            Operation = GameLibraryOperation.Save, DataKey = DataKey,
            ExpectedRevision = baseline.Revision, Game = config
        });
        if (!response.Success) throw new InvalidOperationException(response.Error ?? "设置保存失败；当前调整已保留。");
    }
}
