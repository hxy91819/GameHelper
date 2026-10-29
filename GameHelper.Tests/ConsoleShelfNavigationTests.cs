using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using GameHelper.Core.Models;
using GameHelper.Desktop;

namespace GameHelper.Tests;

public sealed class ConsoleShelfNavigationTests
{
    [Theory]
    [InlineData(1440, 5)]
    [InlineData(640, 2)]
    public Task PageMovesSelectionAndViewportByVisibleCards(double width, int pageSize) => OnSta(() =>
    {
        var shelf = new ListBox();
        ConsoleModeWindow.ConfigureShelf(shelf);
        var surface = new Border { Child = shelf };
        shelf.ItemsSource = Enumerable.Range(0, 20).Select(index => new LibraryGameItem(new LibraryGame
        { Config = new() { DataKey = "game-" + index, DisplayName = "Game " + index } })).ToArray();
        void Layout()
        {
            surface.Measure(new Size(width, 600)); surface.Arrange(new Rect(0, 0, width, 600)); surface.UpdateLayout();
        }
        Layout(); shelf.SelectedIndex = 0;
        var viewer = Find<ScrollViewer>(shelf)!;
        Assert.NotNull(viewer);
        ConsoleModeWindow.PageShelf(shelf, true); Layout();
        Assert.Equal(pageSize, shelf.SelectedIndex);
        // Logical and pixel scrolling use different offset units. Both must advance a whole viewport,
        // rather than the minimal scroll needed to reveal just the new selected card.
        Assert.True(viewer.HorizontalOffset >= viewer.ViewportWidth * .8,
            $"Offset {viewer.HorizontalOffset} did not page across viewport {viewer.ViewportWidth}");
        ConsoleModeWindow.PageShelf(shelf, false); Layout();
        Assert.Equal(0, shelf.SelectedIndex);
        Assert.Equal(0, viewer.HorizontalOffset);
        shelf.SelectedIndex = 18; shelf.ScrollIntoView(shelf.SelectedItem); Layout();
        ConsoleModeWindow.PageShelf(shelf, true); Layout();
        Assert.Equal(19, shelf.SelectedIndex);
        var selected = (ListBoxItem)shelf.ItemContainerGenerator.ContainerFromIndex(19);
        var presenter = Find<ScrollContentPresenter>(shelf)!;
        var position = selected.TranslatePoint(new Point(), presenter);
        Assert.True(position.X >= -1 && position.X + selected.ActualWidth <= presenter.ActualWidth + 1);
    });

    private static T? Find<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            if (child is T match) return match;
            if (Find<T>(child) is { } found) return found;
        }
        return null;
    }
    private static Task OnSta(Action action)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() => { try { action(); completion.SetResult(); } catch (Exception ex) { completion.SetException(ex); } });
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        return completion.Task.WaitAsync(TimeSpan.FromSeconds(15));
    }
}
