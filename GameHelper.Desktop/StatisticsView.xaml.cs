using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace GameHelper.Desktop;

public partial class StatisticsView : UserControl
{
    public StatisticsView() => InitializeComponent();
    internal void FocusSearch() { RankingSearch.Focus(); RankingSearch.SelectAll(); }

    private void OnEditJournal(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (DataContext is StatisticsViewModel model && ((FrameworkElement)sender).DataContext is StatisticsGameRow row)
            new JournalEditor(model.EditJournal(row)) { Owner = Window.GetWindow(this) }.ShowDialog();
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
