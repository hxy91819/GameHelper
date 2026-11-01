using GameHelper.Desktop.Input;
using static GameHelper.Desktop.ControllerButton;

namespace GameHelper.Desktop;

internal sealed partial class ConsoleModeWindow
{
    private readonly ControllerHintBar settingsButtonHint = new() { FontSize = 18 };
    private readonly ControllerHintBar detailSettingsHint = new() { FontSize = 15, Foreground = ConsoleModeVisuals.Secondary };
    private ControllerHintBar? overlayHelp;
    private (ControllerKind Kind, ConsoleOverlay Overlay, bool Launch, bool Detail, bool Running, double Size)? helpState;

    private void UpdateHelp()
    {
        var state = (input.Kind, overlayKind, launchState.ShowOverlay, detail, Selected?.IsRunning == true, help.FontSize);
        if (helpState == state) return;
        helpState = state;
        var kind = input.Kind;
        var hints = overlayKind != ConsoleOverlay.None ? OverlayHints() : launchState.ShowOverlay
            ? [new ControllerHint("关闭提示（保留等待）", Back), new("系统菜单", Menu)]
            : detail
                ? [new ControllerHint(Selected?.IsRunning == true ? "回到游戏" : "启动 / 确认", Confirm), new("游戏设置", Auxiliary),
                    new("返回", Back), new(Selected?.IsRunning == true ? "系统菜单 / 结束游戏" : "系统菜单", Menu)]
                : new ControllerHint[] { new("浏览", Horizontal), new("翻页", LeftTrigger, RightTrigger), new("详情", Confirm),
                    new("游戏设置", Auxiliary), new("退出主机模式", Back), new("搜索", Search), new("页签", LeftShoulder, RightShoulder), new("系统菜单", Menu) };
        help.Set(kind, hints);
        globalHelp.Set(kind, new ControllerHint("全局切换主机 / 桌面", ModeModifier, Menu) { Chord = true });
        overlayHelp?.Set(kind, OverlayHints());
        settingsButtonHint.Set(kind, new ControllerHint("游戏设置", Auxiliary));
        detailSettingsHint.Set(kind, new ControllerHint("快速打开游戏设置", Auxiliary));
    }

    private ControllerHint[] OverlayHints() => overlayKind switch
    {
        ConsoleOverlay.Settings => [new("选择", Vertical), new("调整", Horizontal), new("切换 / 保存", Confirm), new("返回（放弃调整）", Back)],
        ConsoleOverlay.Keyboard => [new("选字符", Directions), new("输入", Confirm), new("退格", Auxiliary), new("完成", Search), new("取消", Back)],
        ConsoleOverlay.Rating => [new("选星", Horizontal), new("提交", Confirm), new("填写想法", Auxiliary), new("跳过", Back)],
        ConsoleOverlay.Confirm => [new("确认执行", Confirm), new("取消", Back)],
        _ => [new("选择", Vertical), new("确认", Confirm), new("关闭菜单", Back)]
    };
}
