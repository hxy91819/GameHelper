using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using GameHelper.Desktop.Input;
using GameHelper.Infrastructure.Processes;

namespace GameHelper.Desktop;

/// <summary>Read-only full-screen library; all launches use the existing catalog command.</summary>
internal sealed partial class ConsoleModeWindow : Window, ILaunchWindowHandoff
{
    private readonly LibraryViewModel model;
    private readonly MainWindow library;
    private readonly ILaunchWindowHandoff previous;
    private readonly ListBox shelf = new();
    private readonly TextBlock heading = new() { FontSize = 44, Text = "今晚，继续冒险。" };
    private readonly TextBlock help = new() { FontSize = 22 };
    private readonly TextBlock info = new() { FontSize = 24, TextWrapping = TextWrapping.Wrap };
    private readonly StackPanel details = new();
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
    private bool ticking;
    private LibraryGameItem? Selected => shelf.SelectedItem as LibraryGameItem;
    internal ConsoleModeWindow(MainWindow library, LibraryViewModel model)
    {
        this.library = library; this.model = model; previous = model.LaunchWindowHandoff;
        Title = "GameHelper 主机模式"; WindowStyle = WindowStyle.None; ResizeMode = ResizeMode.NoResize;
        Background = new SolidColorBrush(Color.FromRgb(13, 23, 34)); Foreground = Brushes.White;
        var root = new DockPanel { Margin = new Thickness(55, 35, 55, 30) }; Content = root;
        var top = new StackPanel(); DockPanel.SetDock(top, Dock.Top); root.Children.Add(top);
        top.Children.Add(new TextBlock { Text = "◈ GAMEHELPER / 主机模式", FontSize = 22, Margin = new Thickness(0,0,0,35) });
        top.Children.Add(heading);
        var tabs = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0,25,0,25) }; top.Children.Add(tabs);
        string[] names = ["全部", "最近", "未通关", "支持手柄"];
        for (int i = 0; i < names.Length; i++) { int index = i; var b = new Button { Content = names[i], FontSize = 22, Padding = new Thickness(22,10,22,10) }; b.Click += (_,_) => { tab = index; Reload(); }; tabs.Children.Add(b); }
        var footer = new StackPanel(); DockPanel.SetDock(footer, Dock.Bottom); root.Children.Add(footer); footer.Children.Add(status); footer.Children.Add(help);
        var body = new Grid(); root.Children.Add(body); body.Children.Add(shelf); body.Children.Add(details); body.Children.Add(launchOverlay); body.Children.Add(actionOverlay);
        shelf.Background = Brushes.Transparent; shelf.BorderThickness = new Thickness(0); shelf.Foreground = Brushes.White;
        shelf.SetValue(ScrollViewer.HorizontalScrollBarVisibilityProperty, ScrollBarVisibility.Auto);
        shelf.SetValue(ScrollViewer.VerticalScrollBarVisibilityProperty, ScrollBarVisibility.Disabled);
        var panel = new FrameworkElementFactory(typeof(StackPanel)); panel.SetValue(StackPanel.OrientationProperty, Orientation.Horizontal); shelf.ItemsPanel = new ItemsPanelTemplate(panel);
        var card = new FrameworkElementFactory(typeof(StackPanel)); card.SetValue(FrameworkElement.WidthProperty, 220d); card.SetValue(FrameworkElement.MarginProperty, new Thickness(10));
        var image = new FrameworkElementFactory(typeof(GamePosterTile)); image.SetValue(FrameworkElement.HeightProperty, 310d); image.SetBinding(GamePosterTile.ArtworkProperty, new Binding("CoverArtwork")); image.SetBinding(GamePosterTile.LabelProperty, new Binding("Name")); card.AppendChild(image);
        foreach (var prop in new[] { "Name", "TotalHours", "Controller.Label" }) { var text = new FrameworkElementFactory(typeof(TextBlock)); text.SetValue(TextBlock.TextWrappingProperty, TextWrapping.Wrap); text.SetValue(TextBlock.FontSizeProperty, 19d); text.SetBinding(TextBlock.TextProperty, new Binding(prop));
            if (prop == "Controller.Label")
            {
                var style = new Style(typeof(TextBlock));
                var red = new DataTrigger { Binding = new Binding("Controller.Unsupported"), Value = true };
                red.Setters.Add(new Setter(TextBlock.ForegroundProperty, Brushes.Salmon)); style.Triggers.Add(red);
                text.SetValue(FrameworkElement.StyleProperty, style);
            }
            card.AppendChild(text); }
        shelf.ItemTemplate = new DataTemplate { VisualTree = card };
        var searchButton = new Button { Content = "搜索 / 屏幕键盘", FontSize = 22, Padding = new Thickness(22,10,22,10) }; searchButton.Click += (_,_) => OpenKeyboard(false); tabs.Children.Add(searchButton);
        shelf.MouseDoubleClick += (_,_) => Handle(ControllerAction.Confirm);
        details.Orientation = Orientation.Horizontal;
        details.Children.Add(detailPoster);
        var detailBody = new StackPanel { Margin = new Thickness(35,0,0,0), MaxWidth = 850 }; details.Children.Add(detailBody);
        detailBody.Children.Add(info); var launch = detailLaunchButton; launch.Click += (_,_) => Handle(ControllerAction.Confirm); detailBody.Children.Add(launch);
        detailBody.Children.Add(stopButton); stopButton.Click += (_,_) => ConfirmStop();
        var back = new Button { Content = "返回", FontSize = 24, HorizontalAlignment = HorizontalAlignment.Left }; back.Click += (_,_) => Handle(ControllerAction.Back); detailBody.Children.Add(back);
        KeyDown += (_,e) => { var a = e.Key switch { Key.Left => ControllerAction.Left, Key.Right => ControllerAction.Right, Key.Enter => ControllerAction.Confirm, Key.Escape => ControllerAction.Back, Key.PageUp => ControllerAction.PreviousPage, Key.PageDown => ControllerAction.NextPage, Key.F2 => ControllerAction.SearchFinish, Key.F3 => ControllerAction.Auxiliary, _ => ControllerAction.None }; if (a != ControllerAction.None) { e.Handled = true; Handle(a); } };
        input = new(this, actions => { UpdateHelp(); foreach (var a in actions) Handle(a); }, message => status.Text = message);
        Loaded += (_,_) => { PlaceOnLibraryMonitor(); library.Hide(); model.LaunchWindowHandoff = this; Reload(); };
        refresh.Tick += Tick; refresh.Start();
        Closed += (_,_) => { closed = true; model.ConsoleLaunchRequestId = null; refresh.Stop(); input.Dispose(); model.LaunchWindowHandoff = previous; library.Show(); library.WindowState = WindowState.Normal; library.Activate(); };
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
        var rows = model.ConsoleGames.ToArray();
        var order = ConsoleModePolicy.Select(rows.Select(g => new ConsoleModePolicy.Entry(g.DataKey,g.LastPlayedAt,model.ConsoleJournal(g.DataKey).Status is "已通关" or "全成就",g.Controller.Status,g.Controller.Manual)),tab,TimeProvider.System).Select(e => e.Key).Where(k => ConsoleInteractionPolicy.Matches(rows.First(g => g.DataKey == k).Name, query));
        shelf.ItemsSource = order.Select(k => rows.First(g => g.DataKey == k)).ToArray();
        shelf.SelectedItem = shelf.Items.Cast<LibraryGameItem>().FirstOrDefault(g => g.DataKey == key);
        if (shelf.SelectedIndex < 0 && shelf.Items.Count > 0) shelf.SelectedIndex = 0;
        heading.Text = $"今晚，继续冒险。 / {new[] { "全部", "最近", "未通关", "支持手柄" }[tab]}";
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
            ConsoleOverlay.SystemMenu => $"上下 选择   {a} 确认   {b} 关闭",
            _ when launchState.ShowOverlay => $"{b} 关闭提示（保留等待）   {(ps ? "Options" : "Start")} 系统菜单",
            _ when detail && Selected?.IsRunning == true => $"{a} 回到游戏   {x} 结束游戏   {b} 返回   {(ps ? "Options" : "Start")} 系统菜单",
            _ when detail => $"{a} 启动 / 确认   {b} 返回   {(ps ? "Options" : "Start")} 系统菜单",
            _ => $"左右 浏览   {a} 详情   {b} 退出主机模式   {y} 搜索   {(ps ? "L1 / R1" : "LB / RB")} 页签   {(ps ? "Options" : "Start")} 系统菜单"
        };
    }
    private void Render()
    {
        launchOverlay.Visibility = launchState.ShowOverlay ? Visibility.Visible : Visibility.Collapsed;
        shelf.Visibility = detail ? Visibility.Collapsed : Visibility.Visible;
        details.Visibility = detail ? Visibility.Visible : Visibility.Collapsed;
        if (Selected is { } posterGame) { detailPoster.Artwork = posterGame.CoverArtwork; detailPoster.Label = posterGame.Name; }
        detailLaunchButton.Content = Selected?.IsRunning == true ? "回到游戏" : "启动游戏 / 确认";
        stopButton.Visibility = detail && Selected?.IsRunning == true ? Visibility.Visible : Visibility.Collapsed;
        if (Selected is { } g) info.Text = $"{g.Name}\n\n{g.Playtime} · {model.ConsoleJournal(g.DataKey).Status}\n{g.LastPlayedLabel} · 最近本局打分：{rating}\n{g.Controller.Label}\n{g.Controller.Description}\n\n启动设置（只读）：{g.Summary} · DS4Windows：{(g.UseDs4Windows ? "启用" : "关闭")}\n\n{(confirm ? "部分支持或不支持手柄，请备好键鼠。确认仍启动 / 返回取消" : g.IsRunning ? "确认回到游戏 · X / □ 结束游戏（需确认）" : "确认启动游戏")}";
        status.Text = launchState.Message ?? (interactionStatus.Length > 0 ? interactionStatus : !string.IsNullOrEmpty(model.ActionError) ? model.ActionError : model.Status);
        RenderActionOverlay(); UpdateHelp();
    }
    private void Handle(ControllerAction action)
    {
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
        if (action is ControllerAction.PreviousPage or ControllerAction.NextPage) { tab = (tab + (action == ControllerAction.NextPage ? 1 : 3)) % 4; Reload(); return; }
        var offset = action == ControllerAction.Left ? -1 : action == ControllerAction.Right ? 1 : 0;
        if (offset != 0 && shelf.Items.Count > 0) { shelf.SelectedIndex = Math.Clamp(shelf.SelectedIndex + offset,0,shelf.Items.Count-1); shelf.ScrollIntoView(shelf.SelectedItem); }
    }
    private async Task LoadRatingAsync(string key)
    {
        try { var value = await model.ConsoleRatingAsync(key); if (!closed && Selected?.DataKey == key) { rating = value; Render(); } }
        catch { if (!closed && Selected?.DataKey == key) { rating = "评分暂不可用"; Render(); } }
    }
    private async void Tick(object? sender, EventArgs e)
    {
        if (closed || ticking) return;
        ticking = true;
        try
        {
            if (launchState.Tracking)
            {
                if (!model.IsBusy && model.HasActionError) launchState.Fail();
                if (launchState.Tracking && !model.IsBusy) await model.RefreshAsync();
                if (closed) return;
                var game = model.ConsoleGames.FirstOrDefault(g => g.DataKey == launchState.Key);
                if (!model.IsBusy && model.HasActionError) launchState.Fail();
                if (game?.LaunchRequestId == launchState.RequestId && game?.IsRunning == true) ratingSession = game.Processes.FirstOrDefault()?.SessionId;
                var effect = launchState.Observe(game?.IsRunning == true, game?.LaunchRequestId, game?.LaunchProcessId);
                Render();
                if (effect == ConsoleLaunchState.Effect.Minimize) WindowState = WindowState.Minimized;
                else if (effect == ConsoleLaunchState.Effect.Restore) { WindowState = WindowState.Normal; Activate(); ratingDeadline = DateTimeOffset.UtcNow.AddSeconds(15); }
            }
            else if (!detail) Reload();
            else Render();
            await CheckRatingInvitationAsync();
        }
        finally { ticking = false; }
    }

    void ILaunchWindowHandoff.AllowForeground() => WindowsForegroundPermission.AllowAnyProcess();
    void ILaunchWindowHandoff.Minimize() { /* Wait for the monitored game, rather than request acceptance. */ }
}
