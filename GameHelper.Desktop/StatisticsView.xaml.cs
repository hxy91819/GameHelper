using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using GameHelper.Core.Models;

namespace GameHelper.Desktop;

public partial class StatisticsView : UserControl
{
    public StatisticsView() => InitializeComponent();
    private GameHelper.Core.Abstractions.IGameLibrary? _library;
    public void Connect(GameHelper.Core.Abstractions.IGameLibrary library, Func<PlaytimeReport, PlaytimeReport>? enrichReport = null)
    { _library = library; ReportPage.Connect(library, enrichReport); }
    private void OnSessionReviews(object sender, RoutedEventArgs e)
    {
        var row = (sender as FrameworkElement)?.DataContext as StatisticsGameRow;
        if (_library is not null) new SessionReviewWindow(_library, row?.DataKey, row?.Name)
            { Owner = Window.GetWindow(this) }.ShowDialog();
    }
    internal void FocusSearch() { RankingSearch.Focus(); RankingSearch.SelectAll(); }

    private async void OnEditJournal(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        var control = (FrameworkElement)sender;
        if (DataContext is not StatisticsViewModel model || control.DataContext is not StatisticsGameRow row) return;
        control.IsEnabled = false;
        try { new JournalEditor(await model.EditJournalAsync(row)) { Owner = Window.GetWindow(this) }.ShowDialog(); }
        catch (Exception ex) { MessageBox.Show(Window.GetWindow(this), "手帐未能读取：" + ex.Message, "GameHelper"); }
        finally { control.IsEnabled = true; }
    }

    private async void OnGameClicked(object sender, MouseButtonEventArgs e)
    {
        if (IsButtonSource(e.OriginalSource as DependencyObject)) return;
        if (DataContext is StatisticsViewModel model && ItemsControl.ContainerFromElement((DataGrid)sender, e.OriginalSource as DependencyObject)
            is DataGridRow { Item: StatisticsGameRow game }) await model.SelectGameAsync(game);
    }
    private async void OnGameKey(object sender, KeyEventArgs e)
    {
        if (IsButtonSource(e.OriginalSource as DependencyObject) || e.Key != Key.Enter || DataContext is not StatisticsViewModel model || ((DataGrid)sender).SelectedItem is not StatisticsGameRow game) return;
        e.Handled = true;
        await model.SelectGameAsync(game);
    }

    private static bool IsButtonSource(DependencyObject? source)
    {
        for (var current = source; current is not null && current is not DataGridRow;)
        {
            if (current is System.Windows.Controls.Primitives.ButtonBase) return true;
            current = current is System.Windows.Media.Visual or System.Windows.Media.Media3D.Visual3D
                ? System.Windows.Media.VisualTreeHelper.GetParent(current) : LogicalTreeHelper.GetParent(current);
        }
        return false;
    }

    private async void OnDaySelected(object? sender, DateOnly date)
    {
        if (DataContext is StatisticsViewModel model) await model.SelectDateAsync(date);
    }

    internal async Task ScrollToCalendarAsync()
    {
        CalendarPanel.BringIntoView();
        await Task.Delay(150);
    }
    internal void ScrollToTop()
    {
        DependencyObject? parent = this;
        while (parent is not null)
        {
            if (parent is ScrollViewer scroll && scroll != PageScroll) { scroll.ScrollToTop(); return; }
            parent = System.Windows.Media.VisualTreeHelper.GetParent(parent);
        }
    }
}
