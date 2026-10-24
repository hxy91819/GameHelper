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

    public MainWindow(LibraryViewModel model)
    {
        _model = model;
        InitializeComponent();
        DataContext = model;
        UpdateThemeButton();
        StatisticsPage.DataContext = model.Statistics;
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
        _showStatistics = statistics;
        Title = statistics ? "GameHelper · 游玩档案" : "GameHelper · 游戏库";
        StatisticsPage.Visibility = statistics ? Visibility.Visible : Visibility.Collapsed;
        LibraryPage.Visibility = statistics ? Visibility.Collapsed : Visibility.Visible;
        LibraryTools.Visibility = statistics ? Visibility.Collapsed : Visibility.Visible;
        if (statistics) LibraryNav.Background = Brushes.Transparent;
        else LibraryNav.SetResourceReference(Control.BackgroundProperty, "NavigationSelected");
        if (statistics) StatisticsNav.SetResourceReference(Control.BackgroundProperty, "NavigationSelected");
        else StatisticsNav.Background = Brushes.Transparent;
        ReloadButton.Content = statistics ? "刷新统计" : "重新载入";
        ReloadButton.ToolTip = statistics ? "重新读取历史游玩记录" : "放弃未保存设置并重新载入";
    }
    private async void OnStatistics(object sender, RoutedEventArgs e) { Navigate(true); await _model.Statistics.EnsureLoadedAsync(); }
    private async void OnLibrary(object sender, RoutedEventArgs e) { _model.SetGenre(null); Navigate(false); await _model.RefreshAsync(); }
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
    private void OnLayout(object sender, RoutedEventArgs e) => SetLayout((string)((Button)sender).Tag);
    private void SetLayout(string layout)
    {
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
        if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.F)
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
        await _model.WaitForArtworkAsync().WaitAsync(TimeSpan.FromSeconds(25));
        Directory.CreateDirectory(directory);
        Capture(Path.Combine(directory, "installed-default.png"));
        GameDetailsScroll.ScrollToVerticalOffset(350); await Task.Delay(100);
        Capture(Path.Combine(directory, "launch-options.png"));
        GameDetailsScroll.ScrollToHome();
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
            try { dialog.Show(); await Task.Delay(150); WindowSnapshot.Save(dialog, Path.Combine(directory, "directory-scan.png")); }
            finally { dialog.Close(); }
        }
        Navigate(true);
        await _model.Statistics.SetRangeAsync(30);
        await _model.Statistics.RefreshAsync();
        if (!_model.Statistics.HasLoaded) throw new InvalidOperationException(_model.Statistics.Status);
        await Task.Delay(200);
        Capture(Path.Combine(directory, "statistics.png"));
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
        await File.WriteAllTextAsync(Path.Combine(directory, "verification.txt"), $"PASS: connected to host; grid/list render; empty search; minimum window size; statistics/trend/calendar/tables render. Posters: {_model.PosterCount}. No settings changed or games launched.");
    }

    private void Capture(string path)
    {
        WindowSnapshot.Save(this, path);
    }
}
