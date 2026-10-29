using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using GameHelper.Core.Models;
using GameHelper.Desktop.Input;
using GameHelper.Infrastructure.Processes;

namespace GameHelper.Desktop;

internal sealed partial class ConsoleModeWindow
{
    private enum ConsoleOverlay { None, SystemMenu, Confirm, Keyboard, Rating, Settings }
    private sealed record MenuRow(string Label, bool Enabled, Func<Task> Execute);
    private readonly Border actionOverlay = new() { Background = new SolidColorBrush(Color.FromArgb(235, 9, 10, 14)), Padding = new Thickness(24), Visibility = Visibility.Collapsed };
    private readonly Button stopButton = new() { Content = "结束游戏（需确认）", FontSize = 24, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0,15,0,0), Visibility = Visibility.Collapsed };
    private ConsoleOverlay overlayKind;
    private List<MenuRow> menuRows = [];
    private int menuIndex;
    private bool actionBusy;
    private string interactionStatus = "";
    private string confirmationText = "";
    private Func<Task>? confirmationAction;
    private ConsoleKeyboard? keyboard;
    private bool editingThought;
    private SessionRatingEditorViewModel? ratingEditor;
    private Guid? ratingSession;
    private DateTimeOffset? ratingDeadline;

    private async Task RunInteractionAsync(Func<Task> action)
    {
        if (actionBusy || closed) return;
        actionBusy = true; interactionStatus = "处理中…"; settingsStatus = ""; Render();
        try { await action(); interactionStatus = ""; }
        catch (Exception ex) { interactionStatus = ex.Message; }
        finally { actionBusy = false; if (!closed) Render(); }
    }
    private async Task<GameLibraryResponse> ConsoleRequestAsync(GameLibraryRequest request)
    {
        var response = await model.ConsoleClient.ExecuteAsync(request);
        if (!response.Success) throw new InvalidOperationException(response.Error ?? "主机模式操作失败。");
        return response;
    }
    private async Task OpenSystemMenuAsync()
    {
        overlayKind = ConsoleOverlay.SystemMenu; menuIndex = 0;
        menuRows = [new("退出主机模式", true, () => { Close(); return Task.CompletedTask; })];
        if (Selected is { } selectedGame)
        {
            var key = selectedGame.DataKey;
            menuRows.Add(new($"当前游戏设置 · {selectedGame.Name}", true, () => OpenGameSettingsAsync(key)));
            if (selectedGame.IsRunning)
                menuRows.Add(new("结束当前游戏（需确认）", true, () => { ConfirmStop(); return Task.CompletedTask; }));
        }
        Render();
        await RunInteractionAsync(async () =>
        {
            var response = await ConsoleRequestAsync(new() { Operation = GameLibraryOperation.ConsoleMenu });
            var hdr = response.ConsoleMenu.FirstOrDefault(r => r.Action == TrayMenuAction.ToggleHdr);
            var pause = response.ConsoleMenu.FirstOrDefault(r => r.Action == TrayMenuAction.Pause30Minutes);
            var resume = response.ConsoleMenu.FirstOrDefault(r => r.Action == TrayMenuAction.ResumeReminders);
            menuRows.Add(new((hdr?.Text ?? "HDR：不可用") + "（显示器临时开关）", ConsoleInteractionPolicy.Available(ConsoleMenuCommand.Hdr,hdr?.Enabled == true,false,false), () => TrayActionAsync(TrayMenuAction.ToggleHdr)));
            var paused = resume?.Enabled == true;
            var reminderCommand = paused ? ConsoleMenuCommand.Resume : ConsoleMenuCommand.Pause;
            menuRows.Add(new(paused ? "恢复休息提醒" : "暂停休息提醒 30 分钟", ConsoleInteractionPolicy.Available(reminderCommand,false,pause?.Enabled == true,paused),
                () => TrayActionAsync(paused ? TrayMenuAction.ResumeReminders : TrayMenuAction.Pause30Minutes)));
        });
        foreach (var command in new[] { ConsoleMenuCommand.Sleep, ConsoleMenuCommand.Restart, ConsoleMenuCommand.Shutdown })
        {
            var captured = command;
            menuRows.Add(new(command switch { ConsoleMenuCommand.Sleep => "睡眠", ConsoleMenuCommand.Restart => "重启电脑", _ => "关闭电脑" },true,
                async () => { await model.RefreshAsync(); ConfirmPower(captured); }));
        }
        if (!closed) Render();
    }
    private async Task TrayActionAsync(TrayMenuAction action)
    {
        await ConsoleRequestAsync(new() { Operation = GameLibraryOperation.ConsoleAction, TrayAction = action });
        overlayKind = ConsoleOverlay.None;
    }
    private void ConfirmPower(ConsoleMenuCommand command)
    {
        if (!ConsoleInteractionPolicy.NeedsConfirmation(command)) return;
        var running = model.ConsoleGames.Where(g => g.IsRunning).Select(g => g.Name).ToArray();
        var label = command switch { ConsoleMenuCommand.Sleep => "睡眠", ConsoleMenuCommand.Restart => "重启电脑", _ => "关闭电脑" };
        confirmationText = $"确认{label}？" + (running.Length == 0 ? "" : $"\n正在运行：{string.Join("、",running)}\n" +
            (command == ConsoleMenuCommand.Sleep ? "睡眠会暂停游戏。" : "这些游戏将被关闭，未保存的进度可能丢失。"));
        confirmationAction = async () => { await Task.Run(() => ConsolePower.Execute(command)); overlayKind = ConsoleOverlay.None; };
        overlayKind = ConsoleOverlay.Confirm; Render();
    }
    private void ConfirmStop()
    {
        if (Selected is not { IsRunning: true } game) return;
        var key = game.DataKey; var session = game.Processes.FirstOrDefault()?.SessionId;
        confirmationText = $"确认结束 {game.Name}？\n未保存的进度可能丢失。";
        confirmationAction = async () =>
        {
            await ConsoleRequestAsync(new() { Operation = GameLibraryOperation.StopGame, DataKey = key, SessionId = session });
            overlayKind = ConsoleOverlay.None;
        };
        overlayKind = ConsoleOverlay.Confirm; Render();
    }
    private async Task FocusSelectedAsync()
    {
        if (Selected is not { IsRunning: true } game) return;
        var request = new GameLibraryRequest { Operation = GameLibraryOperation.FocusGame, DataKey = game.DataKey, SessionId = game.Processes.FirstOrDefault()?.SessionId };
        await RunInteractionAsync(async () =>
        {
            WindowsForegroundPermission.AllowAnyProcess();
            await ConsoleRequestAsync(request); WindowState = WindowState.Minimized;
        });
    }
    private void OpenKeyboard(bool thought)
    {
        if (actionBusy) return;
        editingThought = thought;
        keyboard = new(thought ? ratingEditor?.Thought ?? "" : query, thought ? 2000 : 100);
        overlayKind = ConsoleOverlay.Keyboard; interactionStatus = ""; Render();
    }
    private void FinishKeyboard(bool save)
    {
        if (save && keyboard is not null)
        {
            if (editingThought && ratingEditor is not null) ratingEditor.Thought = keyboard.Text;
            else query = keyboard.Text;
        }
        keyboard = null; overlayKind = editingThought ? ConsoleOverlay.Rating : ConsoleOverlay.None;
        if (!editingThought) { detail = false; Reload(); }
        else Render();
    }
    private async Task CheckRatingInvitationAsync()
    {
        if (ratingDeadline is null || ratingSession is null || overlayKind != ConsoleOverlay.None || actionBusy) return;
        if (DateTimeOffset.UtcNow > ratingDeadline) { ratingDeadline = null; return; }
        try
        {
            var response = await ConsoleRequestAsync(new() { Operation = GameLibraryOperation.ConsoleRating, SessionId = ratingSession });
            if (response.RatedSession is { } session && ConsoleInteractionPolicy.Invite(ratingSession,session.SessionId,true,session.Rating is not null))
            {
                ratingDeadline = null; ratingEditor = new(model.ConsoleClient,session) { Stars = 3 };
                overlayKind = ConsoleOverlay.Rating; Render();
            }
        }
        catch (Exception ex) { interactionStatus = ex.Message; }
    }
    private Task SaveRatingAsync(int? stars = null, bool clear = false) => RunInteractionAsync(async () =>
    {
        if (ratingEditor is not { Busy: false } editor) return;
        if (stars is { } value) editor.Stars = value;
        if (!await editor.SaveAsync(clear)) throw new InvalidOperationException(editor.Status);
        ratingEditor = null; overlayKind = ConsoleOverlay.None;
    });
    private bool HandleActionOverlay(ControllerAction action)
    {
        if (actionBusy) return true;
        if (overlayKind == ConsoleOverlay.None)
        {
            if (action == ControllerAction.Menu) { _ = OpenSystemMenuAsync(); return true; }
            if (action == ControllerAction.SearchFinish && !detail && !launchState.ShowOverlay) { OpenKeyboard(false); return true; }
            if (action == ControllerAction.Auxiliary && !launchState.ShowOverlay && Selected is { } settingsGame)
            { _ = RunInteractionAsync(() => OpenGameSettingsAsync(settingsGame.DataKey)); return true; }
            return false;
        }
        if (overlayKind == ConsoleOverlay.Settings) HandleSettings(action);
        else if (overlayKind == ConsoleOverlay.Keyboard && keyboard is not null)
        {
            if (action == ControllerAction.Back) FinishKeyboard(false);
            else if (action == ControllerAction.SearchFinish) FinishKeyboard(true);
            else if (action == ControllerAction.Auxiliary) keyboard.Delete();
            else if (action == ControllerAction.Confirm)
            {
                var key = ConsoleKeyboard.Keys[keyboard.Index];
                if (key is "完成" or "取消") FinishKeyboard(key == "完成"); else keyboard.Press();
            }
            else keyboard.Move(action == ControllerAction.Left ? -1 : action == ControllerAction.Right ? 1 : 0,
                action == ControllerAction.Up ? -1 : action == ControllerAction.Down ? 1 : 0);
        }
        else if (overlayKind == ConsoleOverlay.SystemMenu)
        {
            if (action is ControllerAction.Back or ControllerAction.Menu) overlayKind = ConsoleOverlay.None;
            else if (action == ControllerAction.Up) menuIndex = Math.Max(0,menuIndex-1);
            else if (action == ControllerAction.Down) menuIndex = Math.Min(menuRows.Count-1,menuIndex+1);
            else if (action == ControllerAction.Confirm && menuRows.ElementAtOrDefault(menuIndex) is { Enabled: true } row) _ = RunInteractionAsync(row.Execute);
        }
        else if (overlayKind == ConsoleOverlay.Confirm)
        {
            if (action == ControllerAction.Back) { confirmationAction = null; overlayKind = ConsoleOverlay.None; }
            else if (action == ControllerAction.Confirm && confirmationAction is { } execute) _ = RunInteractionAsync(execute);
        }
        else if (overlayKind == ConsoleOverlay.Rating && ratingEditor is not null)
        {
            if (action == ControllerAction.Left) ratingEditor.Stars = Math.Max(1,ratingEditor.Stars-1);
            else if (action == ControllerAction.Right) ratingEditor.Stars = Math.Min(5,ratingEditor.Stars+1);
            else if (action == ControllerAction.Back) { ratingEditor = null; overlayKind = ConsoleOverlay.None; }
            else if (action == ControllerAction.Auxiliary) OpenKeyboard(true);
            else if (action == ControllerAction.Confirm) _ = SaveRatingAsync();
        }
        Render(); return true;
    }
    private void RenderActionOverlay()
    {
        actionOverlay.Visibility = overlayKind == ConsoleOverlay.None ? Visibility.Collapsed : Visibility.Visible;
        if (overlayKind == ConsoleOverlay.None) return;
        var panel = new StackPanel();
        var playStation = input.Kind == ControllerKind.PlayStation;
        var confirmButton = playStation ? "×" : "A"; var backButton = playStation ? "○" : "B";
        var auxiliaryButton = playStation ? "□" : "X"; var finishButton = playStation ? "△" : "Y";
        var hint = overlayKind switch
        {
            ConsoleOverlay.Settings => $"上下 选择  ·  左右 调整  ·  {confirmButton} 切换 / 保存  ·  {backButton} 返回（放弃调整）",
            ConsoleOverlay.Keyboard => $"方向键 选字符  ·  {confirmButton} 输入  ·  {auxiliaryButton} 退格  ·  {finishButton} 完成  ·  {backButton} 取消",
            ConsoleOverlay.Rating => $"左右 选星  ·  {confirmButton} 提交  ·  {auxiliaryButton} 填写想法  ·  {backButton} 跳过",
            ConsoleOverlay.Confirm => $"{confirmButton} 确认执行  ·  {backButton} 取消",
            _ => $"上下 选择  ·  {confirmButton} 确认  ·  {backButton} 关闭菜单"
        };
        actionOverlay.Child = ConsoleOverlayVisuals.Surface(panel, hint, interactionStatus);
        Button Button(string label, bool enabled, bool selected, Action click, string? description = null, bool primary = false) =>
            ConsoleOverlayVisuals.Row(label, description, null, selected, enabled && !actionBusy, click, primary);
        if (overlayKind == ConsoleOverlay.Settings) RenderSettingsOverlay(panel);
        else if (overlayKind == ConsoleOverlay.SystemMenu)
        {
            ConsoleOverlayVisuals.Header(panel, "系统菜单", "管理当前游戏、显示与休息提醒。电源操作会再次确认。");
            for (int i=0;i<menuRows.Count;i++)
            {
                var row = menuRows[i];
                var description = row.Label.StartsWith("当前游戏设置", StringComparison.Ordinal) ? "自动 HDR、DS4Windows 与游戏倍速"
                    : row.Label.Contains("临时开关", StringComparison.Ordinal) ? "只调整显示器当前状态，不修改游戏自动 HDR 配置"
                    : row.Label.Contains("结束", StringComparison.Ordinal) || row.Label is "重启电脑" or "关闭电脑" ? "执行前需要确认，请先保存游戏进度"
                    : !row.Enabled ? "当前不可用" : null;
                panel.Children.Add(Button(row.Label,row.Enabled,i==menuIndex,() => _ = RunInteractionAsync(row.Execute),description));
            }
        }
        else if (overlayKind == ConsoleOverlay.Confirm)
        {
            ConsoleOverlayVisuals.Header(panel, "请确认操作", "确认后将执行以下操作。返回可取消。");
            panel.Children.Add(ConsoleOverlayVisuals.Notice(confirmationText, warning: true));
            var actions = new StackPanel { Margin = new Thickness(0, 20, 0, 0) }; panel.Children.Add(actions);
            actions.Children.Add(Button("确认执行",true,true,() => HandleActionOverlay(ControllerAction.Confirm),primary:true));
            actions.Children.Add(Button("取消，返回主机模式",true,false,() => HandleActionOverlay(ControllerAction.Back)));
        }
        else if (overlayKind == ConsoleOverlay.Keyboard && keyboard is not null)
        {
            ConsoleOverlayVisuals.Header(panel, editingThought ? "记录本局想法" : "搜索游戏", "方向键选择字符，确认输入；完成后应用，返回取消。");
            var field = ConsoleModeVisuals.PanelFrame(ConsoleModeVisuals.Label(keyboard.Text.Length == 0 ? "（尚未输入）" : keyboard.Text.Length > 100 ? "…" + keyboard.Text[^100..] : keyboard.Text, 24), 16);
            field.Background = ConsoleModeVisuals.Surface; field.Margin = new Thickness(0, 0, 0, 16); panel.Children.Add(field);
            var grid = new System.Windows.Controls.Primitives.UniformGrid { Columns = ConsoleKeyboard.Columns }; panel.Children.Add(grid);
            for (int i=0;i<ConsoleKeyboard.Keys.Length;i++)
            {
                int index = i;
                grid.Children.Add(ConsoleOverlayVisuals.Key(ConsoleKeyboard.Keys[i],i==keyboard.Index,!actionBusy,() =>
                {
                    if (keyboard is null) return;
                    var key = ConsoleKeyboard.Keys[index];
                    if (key is "完成" or "取消") FinishKeyboard(key == "完成");
                    else { while (keyboard.Index != index) keyboard.Move(keyboard.Index < index ? 1 : -1,0); keyboard.Press(); Render(); }
                }));
            }
        }
        else if (ratingEditor is not null)
        {
            ConsoleOverlayVisuals.Header(panel, "本局体验如何？", ratingEditor.Description);
            var stars = new SessionRatingStars(ratingEditor, console: true) { IsEnabled = !actionBusy };
            stars.StarClicked += value => _ = SaveRatingAsync(value);
            stars.Margin = new Thickness(0, 0, 0, 6); panel.Children.Add(stars);
            var ratingHint = ConsoleModeVisuals.Label("点击星星即保存；可先填写想法。手柄左右选星、确认提交。", 17, ConsoleModeVisuals.Secondary);
            ratingHint.Margin = new Thickness(0, 0, 0, 12); panel.Children.Add(ratingHint);
            panel.Children.Add(ConsoleOverlayVisuals.Notice(ratingEditor.Thought.Length == 0 ? "想法：留空" : ratingEditor.Thought.Length > 100 ? ratingEditor.Thought[..100] + "…" : ratingEditor.Thought));
            if (!string.IsNullOrWhiteSpace(ratingEditor.Status))
                panel.Children.Add(ConsoleOverlayVisuals.Notice(ratingEditor.Status));
            var actions = new StackPanel { Margin = new Thickness(0, 20, 0, 0) }; panel.Children.Add(actions);
            actions.Children.Add(Button("提交评分",true,true,() => HandleActionOverlay(ControllerAction.Confirm),primary:true));
            actions.Children.Add(Button("填写想法",true,false,() => OpenKeyboard(true),"可选，记录当下感受"));
            actions.Children.Add(Button("重载最新评分",true,false,() => _ = RunInteractionAsync(async () =>
            {
                if (ratingEditor is not { } editor) return;
                await editor.ReloadAsync();
            })));
            actions.Children.Add(Button("清除评分",true,false,() => _ = SaveRatingAsync(clear: true)));
            actions.Children.Add(Button("跳过本次评分",true,false,() => HandleActionOverlay(ControllerAction.Back)));
        }
    }
}
