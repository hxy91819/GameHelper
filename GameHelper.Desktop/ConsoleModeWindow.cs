using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using GameHelper.Desktop.Input;
using GameHelper.Infrastructure.Processes;

namespace GameHelper.Desktop;

/// <summary>Full-screen library with controller settings; launches use the existing catalog command.</summary>
internal sealed partial class ConsoleModeWindow : Window, ILaunchWindowHandoff
{
    private readonly LibraryViewModel model;
    private readonly MainWindow library;
    private readonly ILaunchWindowHandoff previous;
    private readonly ListBox shelf = new();
    private readonly TextBlock heading = new() { FontSize = 44, Text = "今晚，继续冒险。" };
    private readonly TextBlock help = new() { FontSize = 22 };
    private readonly TextBlock info = new() { FontSize = 24, TextWrapping = TextWrapping.Wrap };
    private readonly Grid details = new();
    private readonly Button detailLaunchButton = new() { FontSize = 26, Margin = new Thickness(0,25,0,0), HorizontalAlignment = HorizontalAlignment.Left };
    private readonly GamePosterTile detailPoster = new() { Width = 220, Height = 310, HorizontalAlignment = HorizontalAlignment.Left };
    private string rating = "正在读取评分…";
    private bool closed;
    private readonly ConsoleLaunchState launchState = new(TimeProvider.System);
    private readonly Border launchOverlay = new() { Background = new SolidColorBrush(Color.FromArgb(240, 13, 23, 34)), Child = new TextBlock { Text = "启动中…\n正在等待游戏进程 · 返回可关闭提示", FontSize = 32, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center } };
    private readonly TextBlock status = new() { FontSize = 22 };
    private readonly WindowControllerInput input;
    private readonly DispatcherTimer refresh = new() { Interval = TimeSpan.FromSeconds(1) };
    private int tab;
    private bool detail, confirm;
    private string query = "";
    private readonly ConsoleRuntimeRefresh runtimeRefresh;
    private LibraryGameItem? Selected => shelf.SelectedItem as LibraryGameItem;
    internal ConsoleModeWindow(MainWindow library, LibraryViewModel model)
    {
        this.library = library; this.model = model; previous = model.LaunchWindowHandoff;
        Title = "GameHelper 主机模式"; WindowStyle = WindowStyle.None; ResizeMode = ResizeMode.NoResize;
        ConfigureTheme(Resources);
        BuildPresentation();
        KeyDown += (_,e) => { var a = e.Key switch { Key.Left => ControllerAction.Left, Key.Right => ControllerAction.Right, Key.Enter => ControllerAction.Confirm, Key.Escape => ControllerAction.Back, Key.PageUp => ControllerAction.PreviousPage, Key.PageDown => ControllerAction.NextPage, Key.F2 => ControllerAction.SearchFinish, Key.F3 => ControllerAction.Auxiliary, _ => ControllerAction.None }; if (a != ControllerAction.None) { e.Handled = true; Handle(a); } };
        input = new(this, actions => { UpdateHelp(); foreach (var a in actions) Handle(a); }, message => status.Text = message);
        Loaded += (_,_) => { PlaceOnLibraryMonitor(); library.Hide(); model.LaunchWindowHandoff = this; Reload(); };
        runtimeRefresh = new(async () =>
        {
            if (launchState.Tracking && !model.IsBusy && model.HasActionError) launchState.Fail();
            if (!model.IsBusy) await model.RefreshAsync();
        }, RenderRuntimeAsync);
        refresh.Tick += Tick; refresh.Start();
        Closed += (_,_) => { closed = true; runtimeRefresh.Stop(); model.ConsoleLaunchRequestId = null; refresh.Stop(); input.Dispose(); model.LaunchWindowHandoff = previous; library.Show(); library.WindowState = WindowState.Normal; library.Activate(); };
    }
    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct Rect { public int Left, Top, Right, Bottom; }
    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct MonitorInfo { public int Size; public Rect Bounds, Work; public uint Flags; }
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern nint MonitorFromWindow(nint window, uint flags);
    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Auto)] private static extern bool GetMonitorInfo(nint monitor, ref MonitorInfo info);
    private void PlaceOnLibraryMonitor()
    {
        var handle = new System.Windows.Interop.WindowInteropHelper(library).Handle;
        var monitor = MonitorFromWindow(handle, 2);
        var screen = new MonitorInfo { Size = System.Runtime.InteropServices.Marshal.SizeOf<MonitorInfo>() };
        GetMonitorInfo(monitor, ref screen);
        var dpi = VisualTreeHelper.GetDpi(library);
        Left = screen.Bounds.Left / dpi.DpiScaleX; Top = screen.Bounds.Top / dpi.DpiScaleY;
        Width = (screen.Bounds.Right - screen.Bounds.Left) / dpi.DpiScaleX; Height = (screen.Bounds.Bottom - screen.Bounds.Top) / dpi.DpiScaleY;
    }
    private void Reload()
    {
        var key = Selected?.DataKey;
        var rows = model.ConsoleShelfGames.ToArray();
        var order = ConsoleModePolicy.Select(rows.Select(g => new ConsoleModePolicy.Entry(g.DataKey,g.LastPlayedAt,model.ConsoleJournal(g.DataKey).Status is "已通关" or "全成就",g.Controller.Status,g.Controller.Manual)),tab,TimeProvider.System).Select(e => e.Key).Where(k => ConsoleInteractionPolicy.Matches(rows.First(g => g.DataKey == k).Name, query));
        shelf.ItemsSource = order.Select(k => rows.First(g => g.DataKey == k)).ToArray();
        shelf.SelectedItem = shelf.Items.Cast<LibraryGameItem>().FirstOrDefault(g => g.DataKey == key);
        if (key is not null && shelf.SelectedItem is null)
        {
            // A refreshed installation state may remove the open game. Return to the shelf rather
            // than displaying another game's details with the old confirmation or rating.
            detail = false; confirm = false; rating = "正在读取评分…";
            if (overlayKind is ConsoleOverlay.SystemMenu or ConsoleOverlay.Confirm)
            {
                overlayKind = ConsoleOverlay.None; menuRows.Clear(); confirmationAction = null;
            }
        }
        if (shelf.SelectedIndex < 0 && shelf.Items.Count > 0) shelf.SelectedIndex = 0;
        UpdatePresentation();
        Render();
    }
    private void UpdateHelp()
    {
        var ps = input.Kind == ControllerKind.PlayStation;
        var a = ps ? "×" : "A"; var b = ps ? "○" : "B"; var x = ps ? "□" : "X"; var y = ps ? "△" : "Y";
        help.Text = overlayKind switch
        {
            ConsoleOverlay.Keyboard => $"方向键 选字符   {a} 输入   {x} 退格   {y} 完成   {b} 取消",
            ConsoleOverlay.Rating => $"左右 选星   {a} 提交   {x} 填写想法   {b} 跳过",
            ConsoleOverlay.Confirm => $"{a} 确认   {b} 取消",
            ConsoleOverlay.Settings => $"上下 选择   左右 调整   {a} 切换 / 保存   {b} 返回（放弃本页调整）",
            ConsoleOverlay.SystemMenu => $"上下 选择   {a} 确认   {b} 关闭",
            _ when launchState.ShowOverlay => $"{b} 关闭提示（保留等待）   {(ps ? "Options" : "Start")} 系统菜单",
            _ when detail && Selected?.IsRunning == true => $"{a} 回到游戏   {x} 游戏设置   {b} 返回   系统菜单可结束游戏（需确认）   {(ps ? "Options" : "Start")} 系统菜单",
            _ when detail => $"{a} 启动 / 确认   {x} 游戏设置   {b} 返回   {(ps ? "Options" : "Start")} 系统菜单",
            _ => $"左右 浏览   {(ps ? "L2 / R2" : "LT / RT")} 翻页   {a} 详情   {x} 游戏设置   {b} 退出主机模式   {y} 搜索   {(ps ? "L1 / R1" : "LB / RB")} 页签   {(ps ? "Options" : "Start")} 系统菜单"
        };
        help.Text += ps ? "   Share/Create + Options 切换模式（全局）" : "   View/Back + Menu/Start 切换模式（全局）";
        help.TextWrapping = TextWrapping.Wrap;
    }
    private void Render()
    {
        launchOverlay.Visibility = launchState.ShowOverlay ? Visibility.Visible : Visibility.Collapsed;
        shelf.Visibility = detail ? Visibility.Collapsed : Visibility.Visible;
        details.Visibility = detail ? Visibility.Visible : Visibility.Collapsed;
        if (Selected is { } posterGame) { detailPoster.Artwork = posterGame.CoverArtwork; detailPoster.Label = posterGame.Initial; }
        detailLaunchButton.Content = Selected?.IsRunning == true ? "回到游戏" : "启动游戏 / 确认";
        stopButton.Visibility = detail && Selected?.IsRunning == true ? Visibility.Visible : Visibility.Collapsed;
        if (Selected is { } g)
        {
            detailTitle.Text = g.Name;
            detailSummary.Text = $"{g.Playtime}  ·  {model.ConsoleJournal(g.DataKey).Status}\n{g.LastPlayedLabel}  ·  最近本局评分：{rating}";
            detailController.Text = $"{g.Controller.Label}\n{g.Controller.Description}";
            detailSettings.Text = $"{g.Summary}  ·  DS4Windows {(g.UseDs4Windows ? "启用" : "关闭")}\n□ / X 快速打开游戏设置";
            info.Text = confirm ? "这款游戏仅部分支持或不支持手柄。请备好键鼠，再次确认即可启动。" : g.IsRunning ? "游戏正在运行。回到游戏继续冒险，或从系统菜单结束游戏。" : "准备好就出发。启动时会应用已保存的游戏自动化设置。";
        }
        status.Text = launchState.Message ?? (interactionStatus.Length > 0 ? interactionStatus : !string.IsNullOrEmpty(model.ActionError) ? model.ActionError : settingsStatus.Length > 0 ? settingsStatus : model.Status);
        UpdatePresentation(); RenderActionOverlay(); UpdateHelp();
    }
    private void Handle(ControllerAction action)
    {
        if (action == ControllerAction.ToggleConsoleMode) { Close(); return; }
        if (HandleActionOverlay(action)) return;
        if (launchState.ShowOverlay) { if (action == ControllerAction.Back) { launchState.Back(); Render(); } return; }
        if (action == ControllerAction.Back) { launchState.Back(); if (confirm) confirm = false; else if (detail) detail = false; else Close(); Render(); return; }
        if (action == ControllerAction.Confirm)
        {
            if (Selected is not { } game) return;
            if (!detail) { detail = true; rating = "正在读取评分…"; Render(); _ = LoadRatingAsync(game.DataKey); return; }
            if (game.IsRunning) { _ = FocusSelectedAsync(); return; }
            if (launchState.Tracking) { Render(); return; }
            if (!confirm && ConsoleModePolicy.Confirm(game.Controller.Status,game.Controller.Manual)) { confirm = true; Render(); return; }
            model.Selected = game;
            if (!model.LaunchCommand.CanExecute(null)) { Render(); return; }
            if (!launchState.Begin(game.DataKey, game.Name)) return;
            model.ConsoleLaunchRequestId = launchState.RequestId; confirm = false; Render(); model.LaunchFromPoster(); return;
        }
        if (detail) return;
        if (action is ControllerAction.PreviousShelfPage or ControllerAction.NextShelfPage)
        { PageShelf(shelf, action == ControllerAction.NextShelfPage); UpdatePresentation(); return; }
        if (action is ControllerAction.PreviousPage or ControllerAction.NextPage) { tab = (tab + (action == ControllerAction.NextPage ? 1 : 3)) % 4; Reload(); return; }
        var offset = action == ControllerAction.Left ? -1 : action == ControllerAction.Right ? 1 : 0;
        if (offset != 0 && shelf.Items.Count > 0) { shelf.SelectedIndex = Math.Clamp(shelf.SelectedIndex + offset,0,shelf.Items.Count-1); shelf.ScrollIntoView(shelf.SelectedItem); UpdatePresentation(); }
    }
    internal static void PageShelf(ListBox shelf, bool next)
    {
        static T? Find<T>(DependencyObject parent) where T : DependencyObject
        {
            for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
            {
                var child = VisualTreeHelper.GetChild(parent, index);
                if (child is T match) return match;
                if (Find<T>(child) is { } descendant) return descendant;
            }
            return null;
        }
        // ScrollViewer.ViewportWidth may be in logical item units; the presenter is always measured in DIPs.
        var width = Find<ScrollContentPresenter>(shelf)?.ActualWidth ?? shelf.ActualWidth;
        var cardWidth = (shelf.ItemContainerGenerator.ContainerFromIndex(0) as FrameworkElement)?.ActualWidth ?? width;
        if (Find<ScrollViewer>(shelf) is { } scroll) { if (next) scroll.PageRight(); else scroll.PageLeft(); }
        shelf.SelectedIndex = ConsoleInteractionPolicy.ShelfPage(shelf.SelectedIndex, shelf.Items.Count, width, cardWidth, next);
        if (shelf.SelectedItem is { } item) shelf.ScrollIntoView(item);
    }
    private async Task LoadRatingAsync(string key)
    {
        try { var value = await model.ConsoleRatingAsync(key); if (!closed && Selected?.DataKey == key) { rating = value; Render(); } }
        catch { if (!closed && Selected?.DataKey == key) { rating = "评分暂不可用"; Render(); } }
    }
    private async void Tick(object? sender, EventArgs e) => await runtimeRefresh.TickAsync();
    private async Task RenderRuntimeAsync()
    {
        if (closed) return;
        if (launchState.Tracking)
        {
            var game = model.ConsoleGames.FirstOrDefault(g => g.DataKey == launchState.Key);
            if (!model.IsBusy && model.HasActionError) launchState.Fail();
            if (game?.LaunchRequestId == launchState.RequestId && game?.IsRunning == true) ratingSession = game.Processes.FirstOrDefault()?.SessionId;
            var effect = launchState.Observe(game?.IsRunning == true, game?.LaunchRequestId, game?.LaunchProcessId);
            Reload();
            if (effect == ConsoleLaunchState.Effect.Minimize) WindowState = WindowState.Minimized;
            else if (effect == ConsoleLaunchState.Effect.Restore) { WindowForegroundActivation.Restore(this, detail ? detailLaunchButton : shelf); ratingDeadline = DateTimeOffset.UtcNow.AddSeconds(15); }
        }
        else Reload();
        await CheckRatingInvitationAsync();
    }

    void ILaunchWindowHandoff.AllowForeground() => WindowsForegroundPermission.AllowAnyProcess();
    void ILaunchWindowHandoff.Minimize() { /* Wait for the monitored game, rather than request acceptance. */ }
}
