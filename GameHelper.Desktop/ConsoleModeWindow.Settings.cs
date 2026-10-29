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
        panel.Children.Add(new TextBlock { Text = $"{draft.Name} · 游戏自动化设置", FontSize = 34, Foreground = Brushes.White, TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(new TextBlock
        {
            Text = "自动 HDR 配置用于游戏自动化；系统菜单的显示器 HDR 是临时开关。\nDS4Windows 与倍速在下次启动游戏时应用；HDR 在后续自动调度时应用。\n本页读取已保存配置；桌面未保存草稿独立保留。",
            FontSize = 21, Foreground = Brushes.LightGray, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 12, 0, 12)
        });
        string[] labels = [$"自动 HDR：{(draft.Hdr ? "开启" : "关闭")}", $"启动 DS4Windows：{(draft.UseDs4Windows ? "开启" : "关闭")}",
            $"倍速：{(draft.UseSpeed ? "开启" : "关闭")}", $"倍率：{draft.Speed:0.##}×（左右调整 0.1×，范围 0.1–10×）",
            "保存设置", "重新载入已保存配置（放弃本页调整）", "取消（放弃本页调整）"];
        for (var index = 0; index < labels.Length; index++)
        {
            var captured = index;
            var button = new Button { Content = labels[index], FontSize = 24, Padding = new Thickness(15, 8, 15, 8), Margin = new Thickness(3), IsEnabled = !actionBusy,
                Background = index == settingsIndex ? Brushes.LightGreen : Brushes.DarkSlateGray, Foreground = index == settingsIndex ? Brushes.Black : Brushes.White };
            if (index == settingsIndex) button.Loaded += (_, _) => button.BringIntoView();
            button.Click += (_, _) => { settingsIndex = captured; HandleSettings(ControllerAction.Confirm); Render(); };
            panel.Children.Add(button);
        }
        panel.Children.Add(new TextBlock { Text = $"DS4Windows 路径：{draft.Ds4WindowsPath}（路径请在桌面游戏库修改）\n关闭 DS4Windows 会在下次启动此游戏前关闭它。启用倍速需已配置 Cheat Engine。",
            FontSize = 20, Foreground = Brushes.LightGray, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 0) });
    }
}
