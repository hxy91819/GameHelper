using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using GameHelper.Core.Models;
using Microsoft.Win32;

namespace GameHelper.Desktop;

public partial class MainWindow : Window
{
    private void OnAddAction(object sender, RoutedEventArgs e) => _model.Selected?.ActionEditor.Add();
    private void OnRemoveAction(object sender, RoutedEventArgs e)
    { if (sender is FrameworkElement { DataContext: GameActionItem item }) _model.Selected?.ActionEditor.Remove(item); }
    private void OnMoveActionUp(object sender, RoutedEventArgs e)
    { if (sender is FrameworkElement { DataContext: GameActionItem item }) _model.Selected?.ActionEditor.Move(item, -1); }
    private void OnMoveActionDown(object sender, RoutedEventArgs e)
    { if (sender is FrameworkElement { DataContext: GameActionItem item }) _model.Selected?.ActionEditor.Move(item, 1); }
    private void OnChooseAction(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: GameActionItem item }) return;
        var dialog = new OpenFileDialog { Filter = "程序 (*.exe)|*.exe" };
        if (dialog.ShowDialog(this) == true) item.Path = dialog.FileName;
    }

    private void OnRatingHistory(object sender, RoutedEventArgs e)
    {
        if (_model.Selected is { } game) new SessionReviewWindow(_model.Library, game.DataKey) { Owner = this }.ShowDialog();
    }
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
        _model.LaunchWindowHandoff = new MainWindowLaunchWindowHandoff(this);
        SizeChanged += (_, _) => UpdateStudioGeometry();
        Loaded += (_, _) => UpdateStickyDetails();
        DataContext = model;
        UpdateThemeButton();
        SetInstallationGroup(_model.InstallationGroup);
        StatisticsPage.DataContext = model.Statistics;
        StatisticsPage.Connect(model.Library, model.ReportWithJournal);
        GlobalSettingsPage.Connect(model.Library);
        _refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        _refreshTimer.Tick += OnTick;
        _refreshTimer.Start();
        PreviewKeyDown += OnKeyboard;
        Loaded += (_, _) => ConnectControllerLifecycle();
        var closingAfterSave = false;
        Closing += async (_, args) =>
        {
            if (closingAfterSave) return;
            args.Cancel = true;
            IsEnabled = false;
            _refreshTimer.Stop();
            try { await _model.FlushDraftsAsync(); closingAfterSave = true; Dispatcher.BeginInvoke(new Action(Close)); }
            catch (InvalidOperationException ex) { IsEnabled = true; _refreshTimer.Start(); MessageBox.Show(this, ex.Message, "草稿未保存"); }
        };
        Closed += (_, _) => { _refreshTimer.Stop(); _refreshTimer.Tick -= OnTick; DisconnectControllerLifecycle(); _model.Dispose(); };
    }

    private void UpdateThemeButton() => ThemeButton.Content = (Application.Current as App)?.Theme?.IsLight == true ? "深色主题" : "浅色主题";

    private async void OnTheme(object sender, RoutedEventArgs e)
    {
        if ((Application.Current as App)?.Theme is not { } theme) return;
        try { await theme.SetAsync(!theme.IsLight); UpdateThemeButton(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        { _model.Status = "主题偏好未能保存：" + ex.Message; }
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
    private void OnAddMenu(object sender, RoutedEventArgs e) => ShowMenu(sender, [("添加程序…", OnAdd), .. ScanEntries(), ("扫描 Steam…", OnSteam)]);
    private void OnManage(object sender, RoutedEventArgs e) => ShowMenu(sender, [("添加程序…", OnAdd), .. ScanEntries(), ("扫描 Steam…", OnSteam), ("按名称补全海报…", OnMatchMissingPosters), ("所选游戏设置…", OnSettings)]);
    // Remembered roots rescan in one click; MenuItem treats "_" as an access-key marker, so escape it in paths.
    private IEnumerable<(string Label, RoutedEventHandler Action)> ScanEntries() =>
        [("扫描目录…", OnScanDirectory), .. _model.RecentScanDirectories.Select(directory =>
            ("重新扫描 " + directory.Replace("_", "__"), (RoutedEventHandler)(async (_, _) => await ScanAsync(await _model.RescanDirectoryAsync(directory)))))];
    private void OnFiltersMenu(object sender, RoutedEventArgs e) => ShowMenu(sender,
        ("本地未找到", (_, _) => SetInstallationGroup("missing")), ("安装状态待确认", (_, _) => SetInstallationGroup("unknown")),
        ("只看不支持手柄", (_, _) => _model.SetFilter("unsupported")), ("倍速游戏", (_, _) => _model.SetFilter("speed")), ("清除能力筛选", (_, _) => _model.SetFilter("all")));

    private void OnContinue(object sender, RoutedEventArgs e)
    {
        if (_model.ContinueGame is not { } game) return;
        _model.Search = string.Empty; _model.SetFilter("all");
        SelectButton(new[] { AllType, HdrType, SpeedType }, "all");
        SetInstallationGroup("installed"); _model.Selected = game;
        GameList.ScrollIntoView(game);
        if (_model.LaunchCommand.CanExecute(null)) _model.LaunchCommand.Execute(null);
    }
    private void OnPosterMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left && e.ClickCount == 2) _model.LaunchFromPoster();
    }
    private void OnGenre(object sender, RoutedEventArgs e)
    {
        Navigate(false);
        _model.SetGenre((string)((Button)sender).Tag);
        SetInstallationGroup("installed");
    }
    private async void OnEditJournal(object sender, RoutedEventArgs e)
    {
        var control = (FrameworkElement)sender;
        control.IsEnabled = false;
        try { if (await _model.EditSelectedJournalAsync() is { } editor) new JournalEditor(editor) { Owner = this }.ShowDialog(); }
        catch (Exception ex) { _model.Status = "手帐未能读取：" + ex.Message; }
        finally { control.IsEnabled = true; }
    }

    private async void OnControllerManual(object sender, RoutedEventArgs e)
    {
        if (DataContext is LibraryViewModel vm && sender is FrameworkElement element)
            await vm.SetControllerManualAsync((element.Tag as string) switch { "yes" => true, "no" => false, _ => null });
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
        await ScanAsync(await _model.DiscoverDirectoryAsync(folder.FolderName));
    }

    private async Task ScanAsync(LocalGameScan? scan)
    {
        if (scan is null) return;
        var dialog = new DirectoryImportWindow(scan, _model.IgnoredScanPrograms, _model.SetScanIgnored) { Owner = this };
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
    public Task CaptureAsync(string directory) =>
        new MainWindowSnapshot(this, _model, _refreshTimer.Stop, Navigate, SetInstallationGroup, SetLayout, UpdateThemeButton, () => _showStatistics)
            .CaptureAsync(directory);
}
