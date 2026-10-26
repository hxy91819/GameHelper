using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using GameHelper.Core.Models;
using Microsoft.Win32;

namespace GameHelper.Desktop;

public partial class MainWindow : Window
{
    private readonly LibraryViewModel _model;
    private readonly DispatcherTimer _refreshTimer;
    private bool _showStatistics;
    private bool _showGlobalSettings;
    private string _layout = "grid";

    public static readonly DependencyProperty CardColumnsProperty = DependencyProperty.Register(nameof(CardColumns), typeof(int), typeof(MainWindow), new PropertyMetadata(4));
    public int CardColumns { get => (int)GetValue(CardColumnsProperty); set => SetValue(CardColumnsProperty, value); }
    public static readonly DependencyProperty CardPosterHeightProperty = DependencyProperty.Register(nameof(CardPosterHeight), typeof(double), typeof(MainWindow), new PropertyMetadata(246d));
    public double CardPosterHeight { get => (double)GetValue(CardPosterHeightProperty); set => SetValue(CardPosterHeightProperty, value); }
    public static readonly DependencyProperty CardSelectionHeightProperty = DependencyProperty.Register(nameof(CardSelectionHeight), typeof(double), typeof(MainWindow), new PropertyMetadata(254d));
    public double CardSelectionHeight { get => (double)GetValue(CardSelectionHeightProperty); set => SetValue(CardSelectionHeightProperty, value); }
    public static readonly DependencyProperty HeroHeightProperty = DependencyProperty.Register(nameof(HeroHeight), typeof(double), typeof(MainWindow), new PropertyMetadata(243d));
    public double HeroHeight { get => (double)GetValue(HeroHeightProperty); set => SetValue(HeroHeightProperty, value); }
    public static readonly DependencyProperty SidebarWidthProperty = DependencyProperty.Register(nameof(SidebarWidth), typeof(GridLength), typeof(MainWindow), new PropertyMetadata(new GridLength(210)));
    public GridLength SidebarWidth { get => (GridLength)GetValue(SidebarWidthProperty); set => SetValue(SidebarWidthProperty, value); }
    public static readonly DependencyProperty DetailWidthProperty = DependencyProperty.Register(nameof(DetailWidth), typeof(GridLength), typeof(MainWindow), new PropertyMetadata(new GridLength(252)));
    public GridLength DetailWidth { get => (GridLength)GetValue(DetailWidthProperty); set => SetValue(DetailWidthProperty, value); }
    public static readonly DependencyProperty ContentInsetsProperty = DependencyProperty.Register(nameof(ContentInsets), typeof(Thickness), typeof(MainWindow), new PropertyMetadata(new Thickness(36, 32, 36, 32)));
    public Thickness ContentInsets { get => (Thickness)GetValue(ContentInsetsProperty); set => SetValue(ContentInsetsProperty, value); }
    public static readonly DependencyProperty SidebarInsetsProperty = DependencyProperty.Register(nameof(SidebarInsets), typeof(Thickness), typeof(MainWindow), new PropertyMetadata(new Thickness(20, 30, 20, 30)));
    public Thickness SidebarInsets { get => (Thickness)GetValue(SidebarInsetsProperty); set => SetValue(SidebarInsetsProperty, value); }
    public static readonly DependencyProperty BrandFontSizeProperty = DependencyProperty.Register(nameof(BrandFontSize), typeof(double), typeof(MainWindow), new PropertyMetadata(19d));
    public double BrandFontSize { get => (double)GetValue(BrandFontSizeProperty); set => SetValue(BrandFontSizeProperty, value); }
    public static readonly DependencyProperty SplitGapProperty = DependencyProperty.Register(nameof(SplitGap), typeof(GridLength), typeof(MainWindow), new PropertyMetadata(new GridLength(27)));
    public GridLength SplitGap { get => (GridLength)GetValue(SplitGapProperty); set => SetValue(SplitGapProperty, value); }
    private void UpdateStudioGeometry()
    {
        var narrow = ActualWidth <= 1100;
        CardColumns = narrow ? 3 : 4;
        CardPosterHeight = Math.Max(0, GameList.ActualWidth / CardColumns - 16) * 1.5;
        CardSelectionHeight = CardPosterHeight + 8; HeroHeight = ActualWidth >= 1450 ? 295 : 243;
        SidebarInsets = narrow ? new Thickness(13, 26, 13, 26) : new Thickness(20, 30, 20, 30);
        BrandFontSize = narrow ? 16 : 19; SplitGap = new GridLength(narrow ? 20 : 27);
        SidebarWidth = new GridLength(narrow ? 175 : 210); DetailWidth = new GridLength(narrow ? 220 : 252);
        ContentInsets = narrow ? new Thickness(23, 26, 23, 26) : new Thickness(36, 32, 36, 32);
    }

    private void OnGameListSizeChanged(object sender, SizeChangedEventArgs e) => UpdateStudioGeometry();

    private void OnWorkspaceScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (ReferenceEquals(e.OriginalSource, WorkspaceScroll)) UpdateStickyDetails();
    }
    private void OnStickyViewportChanged(object sender, SizeChangedEventArgs e) => UpdateStickyDetails();
    private void UpdateStickyDetails()
    {
        if (StickyDetails is null || DetailHost is null || DetailOffset is null || WorkspaceScroll is null
            || !DetailHost.IsVisible || WorkspaceScroll.ViewportHeight <= 0) return;
        // Measure the unshifted anchor so repeated scroll/layout events cannot accumulate translation.
        var anchorTop = DetailHost.TranslatePoint(new Point(), WorkspaceScroll).Y;
        var top = Math.Max(16, anchorTop);
        DetailOffset.Y = Math.Max(0, 16 - anchorTop);
        StickyDetails.MaxHeight = Math.Max(80, WorkspaceScroll.ViewportHeight - top - 16);
    }

    public MainWindow(LibraryViewModel model)
    {
        _model = model;
        InitializeComponent();
        SizeChanged += (_, _) => UpdateStudioGeometry();
        Loaded += (_, _) => UpdateStickyDetails();
        DataContext = model;
        UpdateThemeButton();
        SetInstallationGroup(_model.InstallationGroup);
        StatisticsPage.DataContext = model.Statistics;
        GlobalSettingsPage.Connect(model.Library);
        _refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        _refreshTimer.Tick += OnTick;
        _refreshTimer.Start();
        PreviewKeyDown += OnKeyboard;
        Closed += (_, _) => { _refreshTimer.Stop(); _refreshTimer.Tick -= OnTick; _model.Dispose(); };
    }

    private void UpdateThemeButton() => ThemeButton.Content = (Application.Current as App)?.Theme?.IsLight == true ? "深色主题" : "浅色主题";

    private void OnTheme(object sender, RoutedEventArgs e)
    {
        if ((Application.Current as App)?.Theme is not { } theme) return;
        try { theme.Set(!theme.IsLight, persist: true); UpdateThemeButton(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { _model.Status = "主题偏好未能保存，请检查数据目录是否可写后重试。"; }
    }

    private async void OnTick(object? sender, EventArgs e) { if (!_showStatistics) await _model.RefreshAsync(); }
    private void Navigate(bool statistics)
    {
        _showGlobalSettings = false;
        GlobalSettingsPage.Visibility = Visibility.Collapsed;
        GlobalSettingsNav.Background = Brushes.Transparent;
        _showStatistics = statistics;
        Title = statistics ? "GameHelper · 游玩档案" : "GameHelper · 游戏库";
        StatisticsPage.Visibility = statistics ? Visibility.Visible : Visibility.Collapsed;
        LibraryPage.Visibility = statistics ? Visibility.Collapsed : Visibility.Visible;
        WorkspaceScroll.ScrollToTop();
        if (!statistics) Dispatcher.BeginInvoke(new Action(UpdateStickyDetails), DispatcherPriority.Loaded);
        if (statistics) LibraryNav.Background = Brushes.Transparent;
        else LibraryNav.SetResourceReference(Control.BackgroundProperty, "NavigationSelected");
        if (statistics) StatisticsNav.SetResourceReference(Control.BackgroundProperty, "NavigationSelected");
        else StatisticsNav.Background = Brushes.Transparent;
        ReloadButton.Content = statistics ? "刷新统计" : "重新载入";
        ReloadButton.ToolTip = statistics ? "重新读取历史游玩记录" : "放弃未保存设置并重新载入";
    }
    private async void OnGlobalSettings(object sender, RoutedEventArgs e)
    {
        _showGlobalSettings = true; _showStatistics = false;
        Title = "GameHelper · 全局设置";
        LibraryPage.Visibility = Visibility.Collapsed; StatisticsPage.Visibility = Visibility.Collapsed;
        GlobalSettingsPage.Visibility = Visibility.Visible;
        LibraryNav.Background = Brushes.Transparent; StatisticsNav.Background = Brushes.Transparent;
        GlobalSettingsNav.SetResourceReference(Control.BackgroundProperty, "NavigationSelected");
        ReloadButton.Content = "重新载入设置"; ReloadButton.ToolTip = "放弃全局设置草稿并重新载入";
        WorkspaceScroll.ScrollToTop(); await GlobalSettingsPage.EnsureLoadedAsync();
    }
    private async void OnStatistics(object sender, RoutedEventArgs e) { Navigate(true); await _model.Statistics.EnsureLoadedAsync(); }
    private async void OnLibrary(object sender, RoutedEventArgs e) { _model.SetGenre(null); SetInstallationGroup("installed"); Navigate(false); await _model.RefreshAsync(); await _model.LoadWeeklyAsync(); }
    private async void OnRefreshLibrary(object sender, RoutedEventArgs e) { if (_showGlobalSettings) { await GlobalSettingsPage.LoadAsync(); return; } await _model.RefreshAsync(); await _model.LoadWeeklyAsync(); }
    private void OnSettings(object sender, RoutedEventArgs e) { if (_model.Selected is not null) SettingsPanel.Visibility = Visibility.Visible; }
    private void OnCloseSettings(object sender, RoutedEventArgs e) => SettingsPanel.Visibility = Visibility.Collapsed;
    private void OnContinueDetails(object sender, RoutedEventArgs e)
    {
        if (_model.ContinueGame is not { } game) return;
        _model.Search = string.Empty; _model.SetFilter("all"); SetInstallationGroup("installed"); _model.Selected = game;
    }
    private void ShowMenu(object sender, params (string Label, RoutedEventHandler Action)[] entries)
    {
        var menu = new ContextMenu { PlacementTarget = (UIElement)sender };
        menu.SetResourceReference(BackgroundProperty, "Surface"); menu.SetResourceReference(ForegroundProperty, "TextPrimary");
        foreach (var entry in entries) { var item = new MenuItem { Header = entry.Label }; item.Click += entry.Action; menu.Items.Add(item); }
        menu.IsOpen = true;
    }
    private void OnAddMenu(object sender, RoutedEventArgs e) => ShowMenu(sender, ("添加程序…", OnAdd), ("扫描目录…", OnScanDirectory), ("扫描 Steam…", OnSteam));
    private void OnManage(object sender, RoutedEventArgs e) => ShowMenu(sender, ("添加程序…", OnAdd), ("扫描目录…", OnScanDirectory), ("扫描 Steam…", OnSteam), ("按名称补全海报…", OnMatchMissingPosters), ("所选游戏设置…", OnSettings));
    private void OnFiltersMenu(object sender, RoutedEventArgs e) => ShowMenu(sender,
        ("本地未找到", (_, _) => SetInstallationGroup("missing")), ("安装状态待确认", (_, _) => SetInstallationGroup("unknown")),
        ("倍速游戏", (_, _) => _model.SetFilter("speed")), ("清除能力筛选", (_, _) => _model.SetFilter("all")));

    private void OnContinue(object sender, RoutedEventArgs e)
    {
        if (_model.ContinueGame is not { } game) return;
        _model.Search = string.Empty; _model.SetFilter("all");
        SelectButton(new[] { AllType, HdrType, SpeedType }, "all");
        SetInstallationGroup("installed"); _model.Selected = game;
        GameList.ScrollIntoView(game);
        if (_model.LaunchCommand.CanExecute(null)) _model.LaunchCommand.Execute(null);
    }
    private void OnGenre(object sender, RoutedEventArgs e)
    {
        Navigate(false);
        _model.SetGenre((string)((Button)sender).Tag);
        SetInstallationGroup("installed");
    }
    private void OnEditGenres(object sender, RoutedEventArgs e)
    {
        if (_model.Selected is not { } game) return;
        var dialog = new GenreEditorWindow(_model, game) { Owner = this };
        dialog.ShowDialog();
    }
    private void OnFilter(object sender, RoutedEventArgs e)
    {
        var filter = (string)((Button)sender).Tag;
        _model.SetFilter(filter);
        SelectButton(new[] { AllType, HdrType, SpeedType }, filter);
    }

    private void SelectButton(IEnumerable<Button> buttons, string selected)
    {
        foreach (var button in buttons)
            button.SetResourceReference(Control.BackgroundProperty, (string)button.Tag == selected ? "SelectedSurface" : "ControlSurface");
    }
    private void OnInstallationGroup(object sender, RoutedEventArgs e) => SetInstallationGroup((string)((Button)sender).Tag);
    private void SetInstallationGroup(string group)
    {
        _model.SetInstallationGroup(group);
        SelectButton(new[] { InstalledGroup, MissingGroup, UnknownGroup, AllGroup }, group);
    }
    private void OnToggleLayout(object sender, RoutedEventArgs e) => SetLayout(_layout == "grid" ? "list" : "grid");
    private void OnLayout(object sender, RoutedEventArgs e) => SetLayout((string)((Button)sender).Tag);
    private void SetLayout(string layout)
    {
        _layout = layout;
        SelectButton(new[] { GridLayout, ListLayout }, layout);
        GameList.ItemTemplate = (DataTemplate)Resources[layout == "grid" ? "CardTemplate" : "RowTemplate"];
        GameList.ItemsPanel = (ItemsPanelTemplate)Resources[layout == "grid" ? "CardsPanel" : "RowsPanel"];
    }

    private async void OnAdd(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Title = "选择用于识别游戏的 exe", Filter = "游戏程序|*.exe", CheckFileExists = true };
        if (dialog.ShowDialog(this) == true) await _model.AddAsync(dialog.FileName);
    }

    private async void OnPoster(object sender, RoutedEventArgs e)
    {
        if (_model.Selected is not { } game) return;
        var dialog = new SteamPosterWindow(_model, game.Name) { Owner = this };
        if (dialog.ShowDialog() != true) return;
        try { await _model.AssociatePosterAsync(game, dialog.SelectedAppId); }
        catch (Exception ex) { _model.Status = "海报关联未能保存：" + ex.Message; }
    }

    private async void OnScanDirectory(object sender, RoutedEventArgs e)
    {
        var folder = new OpenFolderDialog { Title = "选择游戏库目录或单个游戏目录", Multiselect = false };
        if (folder.ShowDialog(this) != true) return;
        var scan = await _model.DiscoverDirectoryAsync(folder.FolderName);
        if (scan is null) return;
        var dialog = new DirectoryImportWindow(scan) { Owner = this };
        if (dialog.ShowDialog() == true) await _model.ImportDirectoryAsync(dialog.SelectedGames);
    }

    private void OnMatchMissingPosters(object sender, RoutedEventArgs e)
    {
        if (_model.Ready) new MissingPostersWindow(_model) { Owner = this }.ShowDialog();
    }

    private void OnChooseEntry(object sender, RoutedEventArgs e)
    {
        if (_model.Selected is null) return;
        var dialog = new OpenFileDialog { Title = "选择原本使用的游戏启动入口", Filter = "游戏程序或快捷方式|*.exe;*.lnk;*.url", CheckFileExists = true };
        if (dialog.ShowDialog(this) == true) _model.Selected.Target = dialog.FileName;
    }

    private void OnChooseDs4Windows(object sender, RoutedEventArgs e)
    {
        if (_model.Selected is null) return;
        var dialog = new OpenFileDialog { Title = "选择 DS4Windows 程序", Filter = "程序|*.exe", CheckFileExists = true };
        if (dialog.ShowDialog(this) == true) _model.Selected.Ds4WindowsPath = dialog.FileName;
    }

    private async void OnRemove(object sender, RoutedEventArgs e)
    {
        if (_model.Selected is not { } game) return;
        if (MessageBox.Show(this, $"从目录移除“{game.Name}”？历史游玩记录会保留。", "移除游戏", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
            await _model.RemoveAsync();
    }

    private async void OnReload(object sender, RoutedEventArgs e)
    {
        if (_showGlobalSettings) { await GlobalSettingsPage.LoadAsync(); return; }
        if (_showStatistics) { await _model.Statistics.RefreshAsync(); return; }
        if (_model.HasUnsavedChanges && MessageBox.Show(this, "放弃未保存的修改并重新载入？", "重新载入", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        await _model.RefreshAsync(discardDrafts: true);
    }

    private async void OnSteam(object sender, RoutedEventArgs e)
    {
        var games = await _model.DiscoverAsync();
        if (games.Count == 0) return;
        var dialog = new SteamImportWindow(games) { Owner = this };
        if (dialog.ShowDialog() == true) await _model.ImportAsync(dialog.SelectedAppIds);
    }

    private void OnKeyboard(object sender, KeyEventArgs e)
    {
        if (!_showGlobalSettings && Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.F)
        {
            if (_showStatistics) StatisticsPage.FocusSearch();
            else { SearchBox.Focus(); SearchBox.SelectAll(); }
            e.Handled = true;
        }
        else if (e.Key == Key.Escape && SearchBox.IsKeyboardFocused) SearchBox.Clear();
    }

    /// <summary>Read-only rendering probe against an explicitly selected running host.</summary>
    public async Task CaptureAsync(string directory)
    {
        var width = Width; var height = Height;
        var theme = (Application.Current as App)?.Theme;
        var originalLight = theme?.IsLight == true;
        try
        {
            await CaptureThemeAsync(directory);
            if (theme is not null)
            {
                theme.Set(!originalLight, persist: false);
                Width = width; Height = height;
                Navigate(false); SetInstallationGroup("installed"); SetLayout("grid");
                UpdateThemeButton();
                await CaptureThemeAsync(Path.Combine(directory, originalLight ? "dark" : "light"));
            }
        }
        finally
        {
            theme?.Set(originalLight, persist: false);
            UpdateThemeButton();
        }
        Capture(Path.Combine(directory, "theme-restored.png"));
    }

    private async Task CaptureThemeAsync(string directory)
    {
        var originalWidth = Width;
        var originalHeight = Height;
        _refreshTimer.Stop();
        await _model.LoadWeeklyAsync();
        await _model.WaitForArtworkAsync().WaitAsync(TimeSpan.FromSeconds(25));
        Directory.CreateDirectory(directory);
        Capture(Path.Combine(directory, "installed-default.png"));
        CaptureGeometry(Path.Combine(directory, "geometry-library.json"));
        await VerifyPageWheelAsync(GameList, Path.Combine(directory, "posters-scroll.png"));
        SettingsPanel.Visibility = Visibility.Visible; GameDetailsScroll.ScrollToVerticalOffset(350); await Task.Delay(100);
        Capture(Path.Combine(directory, "launch-options.png"));
        GameDetailsScroll.ScrollToHome(); SettingsPanel.Visibility = Visibility.Collapsed;
        SetInstallationGroup("all");
        foreach (var layout in new[] { "grid", "list" })
        {
            SetLayout(layout);
            UpdateLayout();
            await Task.Delay(200);
            Capture(Path.Combine(directory, layout + ".png"));
        }
        _model.Search = "__no_game_matches_this_query__";
        await Task.Delay(100);
        if (!_model.Games.IsEmpty) throw new InvalidOperationException("搜索空结果未生效。");
        Capture(Path.Combine(directory, "empty.png"));
        _model.Search = string.Empty;
        Width = MinWidth; Height = MinHeight;
        await Task.Delay(100);
        Capture(Path.Combine(directory, "small-window.png"));
        SetLayout("grid"); await Task.Delay(100); Capture(Path.Combine(directory, "small-grid.png"));
        Width = originalWidth; Height = originalHeight;
        SetInstallationGroup("missing"); await Task.Delay(100); Capture(Path.Combine(directory, "missing-games.png"));
        SetInstallationGroup("unknown"); await Task.Delay(100); Capture(Path.Combine(directory, "unknown-games.png"));
        SetInstallationGroup("all");
        if (Environment.GetEnvironmentVariable("GAMEHELPER_TEST_SCAN_DIRECTORY") is { Length: > 0 } scanDirectory)
        {
            var scan = await _model.DiscoverDirectoryAsync(scanDirectory) ?? throw new InvalidOperationException(_model.Status);
            var dialog = new DirectoryImportWindow(scan) { Owner = this, ShowActivated = false, ShowInTaskbar = false };
            WindowSnapshot.PrepareOffscreen(dialog);
            try
            {
                dialog.Show(); await Task.Delay(150);
                if (dialog.SelectedGames.Count != 0) throw new InvalidOperationException("扫描结果不应默认勾选任何程序。");
                WindowSnapshot.Save(dialog, Path.Combine(directory, "directory-scan.png"));
                dialog.ShowAuxiliaryCandidates(); await Task.Delay(150);
                WindowSnapshot.Save(dialog, Path.Combine(directory, "directory-scan-auxiliary.png"));
            }
            finally { dialog.Close(); }
        }
        Navigate(true);
        await _model.Statistics.SetRangeAsync(30);
        await _model.Statistics.RefreshAsync();
        if (!_model.Statistics.HasLoaded) throw new InvalidOperationException(_model.Statistics.Status);
        await Task.Delay(200);
        Capture(Path.Combine(directory, "statistics.png"));
        await VerifyPageWheelAsync((FrameworkElement)StatisticsPage.FindName("RankingTable"), Path.Combine(directory, "statistics-scroll.png"));
        await _model.Statistics.SetRangeAsync(0);
        if (_model.Statistics.Days != 0) throw new InvalidOperationException(_model.Statistics.Status);
        await Task.Delay(150);
        Capture(Path.Combine(directory, "statistics-all.png"));
        await StatisticsPage.ScrollToCalendarAsync();
        Capture(Path.Combine(directory, "statistics-calendar.png"));
        StatisticsPage.ScrollToTop();
        Width = MinWidth; Height = MinHeight;
        await Task.Delay(150);
        Capture(Path.Combine(directory, "statistics-small.png"));
        Navigate(false);
        await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
        if (Math.Abs(DetailOffset.Y) > .5) throw new InvalidOperationException("返回游戏库后详情吸顶位置未重置。");
        Capture(Path.Combine(directory, "library-return.png"));
        await File.WriteAllTextAsync(Path.Combine(directory, "verification.txt"), $"PASS: connected to host; grid/list render; empty search; minimum window size; statistics/trend/calendar/tables render. Posters: {_model.PosterCount}. No settings changed or games launched.");
    }

    private async Task VerifyPageWheelAsync(FrameworkElement target, string screenshot)
    {
        WorkspaceScroll.ScrollToTop();
        await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
        var wheel = new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, -960)
        { RoutedEvent = Mouse.PreviewMouseWheelEvent };
        target.RaiseEvent(wheel);
        await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
        if (!wheel.Handled || WorkspaceScroll.VerticalOffset <= 0)
            throw new InvalidOperationException("内层控件上的滚轮未滚动整页。");
        if (!_showStatistics)
        {
            var top = StickyDetails.TranslatePoint(new Point(), WorkspaceScroll).Y;
            if (top < 15.5 || top + StickyDetails.ActualHeight > WorkspaceScroll.ViewportHeight - 15.5)
                throw new InvalidOperationException("右侧详情未保持在可见视口内。");
        }
        Capture(screenshot);
        if (!_showStatistics)
        {
            WorkspaceScroll.ScrollToBottom();
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
            var top = StickyDetails.TranslatePoint(new Point(), WorkspaceScroll).Y;
            if (top < 15.5 || top + StickyDetails.ActualHeight > WorkspaceScroll.ViewportHeight - 15.5)
                throw new InvalidOperationException("海报列表底部的详情未保持可见。");
            Capture(Path.Combine(Path.GetDirectoryName(screenshot)!, "posters-bottom.png"));
        }
        WorkspaceScroll.ScrollToTop();
        await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
    }

    private void CaptureGeometry(string path)
    {
        var controls = new FrameworkElement[] { LibraryNav, StatisticsNav, LibraryPage, HeroPanel, DetailCard, SearchBox, GameList, StatisticsPage };
        var geometry = controls.Where(control => control.IsVisible).Select(control =>
        {
            var position = control.TranslatePoint(new Point(), this);
            return new { control.Name, X = position.X, Y = position.Y, Width = control.ActualWidth, Height = control.ActualHeight };
        });
        File.WriteAllText(path, System.Text.Json.JsonSerializer.Serialize(new { Width = ActualWidth, Height = ActualHeight, Controls = geometry }));
    }

    private void Capture(string path)
    {
        WindowSnapshot.Save(this, path);
    }
}
