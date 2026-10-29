using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using GameHelper.Core.Models;
using GameHelper.Desktop.Input;

namespace GameHelper.Desktop;

internal sealed partial class ConsoleModeWindow
{
    private ConsoleGameSettings? gameSettings;
    private int settingsIndex;
    private string settingsStatus = "";

    private async Task OpenGameSettingsAsync(string key)
    {
        var response = await ConsoleRequestAsync(new() { Operation = GameLibraryOperation.List });
        var row = response.Games.FirstOrDefault(game => string.Equals(game.Config.DataKey, key, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException("游戏已从目录移除，请刷新游戏库。");
        gameSettings = new(model.ConsoleClient, row);
        settingsIndex = 0; settingsStatus = "";
        overlayKind = ConsoleOverlay.Settings;
    }

    private void HandleSettings(ControllerAction action)
    {
        if (gameSettings is not { } draft) return;
        if (action is ControllerAction.Back or ControllerAction.Menu)
        {
            gameSettings = null; overlayKind = ConsoleOverlay.None; return;
        }
        if (action == ControllerAction.Up) settingsIndex = Math.Max(0, settingsIndex - 1);
        else if (action == ControllerAction.Down) settingsIndex = Math.Min(6, settingsIndex + 1);
        else if (action is ControllerAction.Left or ControllerAction.Right or ControllerAction.Confirm)
        {
            if (settingsIndex == 0) draft.Hdr = !draft.Hdr;
            else if (settingsIndex == 1) draft.UseDs4Windows = !draft.UseDs4Windows;
            else if (settingsIndex == 2) draft.UseSpeed = !draft.UseSpeed;
            else if (settingsIndex == 3) draft.AdjustSpeed(action == ControllerAction.Left ? -1 : 1);
            else if (action == ControllerAction.Confirm && settingsIndex == 4)
                _ = RunInteractionAsync(async () =>
                {
                    await draft.SaveAsync();
                    gameSettings = null; overlayKind = ConsoleOverlay.None;
                    // Keep a distinct success message: RunInteractionAsync clears its transient status.
                    settingsStatus = ConsoleGameSettings.SavedMessage;
                    await model.RefreshAsync(); Reload();
                });
            else if (action == ControllerAction.Confirm && settingsIndex == 5)
                _ = RunInteractionAsync(() => OpenGameSettingsAsync(draft.DataKey));
            else if (action == ControllerAction.Confirm && settingsIndex == 6)
            { gameSettings = null; overlayKind = ConsoleOverlay.None; }
        }
    }

    private void RenderSettingsOverlay(StackPanel panel)
    {
        if (gameSettings is not { } draft) return;
        ConsoleOverlayVisuals.Header(panel, "游戏自动化设置", $"{draft.Name}\nDS4Windows 与倍速下次启动生效；HDR 在后续自动调度时应用。");
        string[] labels = ["自动 HDR", "启动 DS4Windows", "游戏倍速", "倍速倍率", "保存设置", "重新载入已保存配置", "取消，返回主机模式"];
        string?[] values = [draft.Hdr ? "开启" : "关闭", draft.UseDs4Windows ? "开启" : "关闭", draft.UseSpeed ? "开启" : "关闭", $"{draft.Speed:0.##}×", null, null, null];
        string[] descriptions = ["用于游戏自动化，与显示器临时 HDR 开关独立", "在启动游戏前准备手柄配套软件", "启用后使用已配置的 Cheat Engine", "左右调整 0.1× · 范围 0.1–10×",
            "应用本页调整，桌面未保存草稿独立保留", "放弃本页调整，读取最新设置", "放弃本页尚未保存的调整"];
        for (var index = 0; index < labels.Length; index++)
        {
            var captured = index;
            panel.Children.Add(ConsoleOverlayVisuals.Row(labels[index], descriptions[index], values[index], index == settingsIndex, !actionBusy,
                () => { settingsIndex = captured; HandleSettings(ControllerAction.Confirm); Render(); }, primary: index == 4));
        }
        panel.Children.Add(ConsoleOverlayVisuals.Notice("关闭 DS4Windows 会在下次启动此游戏前关闭它。"));
        var path = ConsoleModeVisuals.Label($"DS4Windows 路径（在桌面游戏库修改）\n{draft.Ds4WindowsPath}", 15, ConsoleModeVisuals.Secondary);
        path.Margin = new Thickness(2, 14, 2, 2); panel.Children.Add(path);
    }
}
