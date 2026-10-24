using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace GameHelper.Desktop;

public partial class StatisticsView : UserControl
{
    public StatisticsView() => InitializeComponent();
    internal void FocusSearch() { RankingSearch.Focus(); RankingSearch.SelectAll(); }

    private async void OnGameClicked(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is StatisticsViewModel model && ItemsControl.ContainerFromElement((DataGrid)sender, e.OriginalSource as DependencyObject)
            is DataGridRow { Item: StatisticsGameRow game }) await model.SelectGameAsync(game);
    }
    private async void OnGameKey(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || DataContext is not StatisticsViewModel model || ((DataGrid)sender).SelectedItem is not StatisticsGameRow game) return;
        e.Handled = true;
        await model.SelectGameAsync(game);
    }

    private async void OnDaySelected(object? sender, DateOnly date)
    {
        if (DataContext is StatisticsViewModel model) await model.SelectDateAsync(date);
    }

    internal async Task ScrollToCalendarAsync()
    {
        PageScroll.ScrollToBottom();
        await Task.Delay(150);
    }
    internal void ScrollToTop() => PageScroll.ScrollToTop();
}
