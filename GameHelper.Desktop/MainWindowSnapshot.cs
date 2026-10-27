using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;

namespace GameHelper.Desktop;

/// <summary>Read-only rendering and interaction probes, separate from normal window event wiring.</summary>
internal sealed class MainWindowSnapshot(MainWindow window, LibraryViewModel model, Action stopRefresh,
    Action<bool> navigate, Action<string> setInstallationGroup, Action<string> setLayout, Action updateThemeButton, Func<bool> showingStatistics)
{
    /// <summary>Read-only rendering probe against an explicitly selected running host.</summary>
    public async Task CaptureAsync(string directory)
    {
        var width = window.Width; var height = window.Height;
        var theme = (Application.Current as App)?.Theme;
        var originalLight = theme?.IsLight == true;
        try
        {
            await CaptureThemeAsync(directory);
            if (theme is not null)
            {
                theme.Set(!originalLight, persist: false);
                window.Width = width; window.Height = height;
                navigate(false); setInstallationGroup("installed"); setLayout("grid");
                updateThemeButton();
                await CaptureThemeAsync(Path.Combine(directory, originalLight ? "dark" : "light"));
            }
        }
        finally
        {
            theme?.Set(originalLight, persist: false);
            updateThemeButton();
        }
        Capture(Path.Combine(directory, "theme-restored.png"));
    }

    private async Task CaptureThemeAsync(string directory)
    {
        var originalWidth = window.Width;
        var originalHeight = window.Height;
        stopRefresh();
        await model.LoadWeeklyAsync();
        await model.WaitForArtworkAsync().WaitAsync(TimeSpan.FromSeconds(25));
        Directory.CreateDirectory(directory);
        Capture(Path.Combine(directory, "installed-default.png"));
        CaptureGeometry(Path.Combine(directory, "geometry-library.json"));
        await VerifyPageWheelAsync(window.GameList, Path.Combine(directory, "posters-scroll.png"));
        window.SettingsPanel.Visibility = Visibility.Visible; window.GameDetailsScroll.ScrollToVerticalOffset(350); await Task.Delay(100);
        Capture(Path.Combine(directory, "launch-options.png"));
        window.GameDetailsScroll.ScrollToHome(); window.SettingsPanel.Visibility = Visibility.Collapsed;
        setInstallationGroup("all");
        foreach (var layout in new[] { "grid", "list" })
        {
            setLayout(layout);
            window.UpdateLayout();
            await Task.Delay(200);
            Capture(Path.Combine(directory, layout + ".png"));
        }
        model.Search = "__no_game_matches_this_query__";
        await Task.Delay(100);
        if (!model.Games.IsEmpty) throw new InvalidOperationException("搜索空结果未生效。");
        Capture(Path.Combine(directory, "empty.png"));
        model.Search = string.Empty;
        window.Width = window.MinWidth; window.Height = window.MinHeight;
        await Task.Delay(100);
        Capture(Path.Combine(directory, "small-window.png"));
        setLayout("grid"); await Task.Delay(100); Capture(Path.Combine(directory, "small-grid.png"));
        window.Width = originalWidth; window.Height = originalHeight;
        setInstallationGroup("missing"); await Task.Delay(100); Capture(Path.Combine(directory, "missing-games.png"));
        setInstallationGroup("unknown"); await Task.Delay(100); Capture(Path.Combine(directory, "unknown-games.png"));
        setInstallationGroup("all");
        if (Environment.GetEnvironmentVariable("GAMEHELPER_TEST_SCAN_DIRECTORY") is { Length: > 0 } scanDirectory)
        {
            var scan = await model.DiscoverDirectoryAsync(scanDirectory) ?? throw new InvalidOperationException(model.Status);
            var dialog = new DirectoryImportWindow(scan, model.IgnoredScanPrograms) { Owner = window, ShowActivated = false, ShowInTaskbar = false };
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
        navigate(true);
        await model.Statistics.SetRangeAsync(30);
        await model.Statistics.RefreshAsync();
        if (!model.Statistics.HasLoaded) throw new InvalidOperationException(model.Statistics.Status);
        await Task.Delay(200);
        Capture(Path.Combine(directory, "statistics.png"));
        await VerifyPageWheelAsync((FrameworkElement)window.StatisticsPage.FindName("RankingTable"), Path.Combine(directory, "statistics-scroll.png"));
        await model.Statistics.SetRangeAsync(0);
        if (model.Statistics.Days != 0) throw new InvalidOperationException(model.Statistics.Status);
        await Task.Delay(150);
        Capture(Path.Combine(directory, "statistics-all.png"));
        await window.StatisticsPage.ScrollToCalendarAsync();
        Capture(Path.Combine(directory, "statistics-calendar.png"));
        window.StatisticsPage.ScrollToTop();
        window.Width = window.MinWidth; window.Height = window.MinHeight;
        await Task.Delay(150);
        Capture(Path.Combine(directory, "statistics-small.png"));
        navigate(false);
        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
        if (Math.Abs(window.DetailOffset.Y) > .5) throw new InvalidOperationException("返回游戏库后详情吸顶位置未重置。");
        Capture(Path.Combine(directory, "library-return.png"));
        await File.WriteAllTextAsync(Path.Combine(directory, "verification.txt"), $"PASS: connected to host; grid/list render; empty search; minimum window size; statistics/trend/calendar/tables render. Posters: {model.PosterCount}. No settings changed or games launched.");
    }

    private async Task VerifyPageWheelAsync(FrameworkElement target, string screenshot)
    {
        window.WorkspaceScroll.ScrollToTop();
        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
        var wheel = new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, -960)
        { RoutedEvent = Mouse.PreviewMouseWheelEvent };
        target.RaiseEvent(wheel);
        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
        if (!wheel.Handled || window.WorkspaceScroll.VerticalOffset <= 0)
            throw new InvalidOperationException("内层控件上的滚轮未滚动整页。");
        if (!showingStatistics())
        {
            var top = window.StickyDetails.TranslatePoint(new Point(), window.WorkspaceScroll).Y;
            if (top < 15.5 || top + window.StickyDetails.ActualHeight > window.WorkspaceScroll.ViewportHeight - 15.5)
                throw new InvalidOperationException("右侧详情未保持在可见视口内。");
        }
        Capture(screenshot);
        if (!showingStatistics())
        {
            window.WorkspaceScroll.ScrollToBottom();
            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
            var top = window.StickyDetails.TranslatePoint(new Point(), window.WorkspaceScroll).Y;
            if (top < 15.5 || top + window.StickyDetails.ActualHeight > window.WorkspaceScroll.ViewportHeight - 15.5)
                throw new InvalidOperationException("海报列表底部的详情未保持可见。");
            Capture(Path.Combine(Path.GetDirectoryName(screenshot)!, "posters-bottom.png"));
        }
        window.WorkspaceScroll.ScrollToTop();
        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
    }

    private void CaptureGeometry(string path)
    {
        var controls = new FrameworkElement[] { window.LibraryNav, window.StatisticsNav, window.LibraryPage, window.HeroPanel, window.DetailCard, window.SearchBox, window.GameList, window.StatisticsPage };
        var geometry = controls.Where(control => control.IsVisible).Select(control =>
        {
            var position = control.TranslatePoint(new Point(), window);
            return new { control.Name, X = position.X, Y = position.Y, Width = control.ActualWidth, Height = control.ActualHeight };
        });
        File.WriteAllText(path, System.Text.Json.JsonSerializer.Serialize(new { Width = window.ActualWidth, Height = window.ActualHeight, Controls = geometry }));
    }

    private void Capture(string path)
    {
        WindowSnapshot.Save(window, path);
    }
}
