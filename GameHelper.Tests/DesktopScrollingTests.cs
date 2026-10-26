using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using GameHelper.Desktop;

namespace GameHelper.Tests;

public sealed class DesktopScrollingTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task WheelOverNestedItemsScrollsWholePage(bool statistics) => OnSta(() =>
    {
        Control items = statistics ? new DataGrid { Height = 700 } : new ListBox { Height = 700 };
        var nested = new ScrollViewer { Content = items, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled };
        var page = new ScrollViewer { Content = nested, Height = 200, Width = 400 };
        PageWheelScroll.SetEnabled(page, true);
        page.Measure(new Size(400, 200)); page.Arrange(new Rect(0, 0, 400, 200)); page.UpdateLayout();
        Assert.True(page.ScrollableHeight > 0);
        var wheel = new MouseWheelEventArgs(Mouse.PrimaryDevice, 0, -120) { RoutedEvent = Mouse.PreviewMouseWheelEvent };
        items.RaiseEvent(wheel);
        page.UpdateLayout();
        Assert.True(wheel.Handled);
        Assert.True(page.VerticalOffset > 0);
        Assert.Equal(0, nested.VerticalOffset);
    });

    [Fact]
    public Task IndependentDetailsScrollLocallyThenForwardAtBoundary() => OnSta(() =>
    {
        var target = new Border { Height = 900 };
        var details = new ScrollViewer { Height = 200, Content = target };
        PageWheelScroll.SetIndependent(details, true);
        var page = new ScrollViewer { Content = new StackPanel { Children = { details, new Border { Height = 900 } } } };
        PageWheelScroll.SetEnabled(page, true);
        page.Measure(new Size(400, 300)); page.Arrange(new Rect(0, 0, 400, 300)); page.UpdateLayout();
        void Wheel() { target.RaiseEvent(new MouseWheelEventArgs(Mouse.PrimaryDevice, 0, -120)
            { RoutedEvent = Mouse.PreviewMouseWheelEvent }); page.UpdateLayout(); }
        Wheel();
        Assert.True(details.VerticalOffset > 0); Assert.Equal(0, page.VerticalOffset);
        details.ScrollToBottom(); page.UpdateLayout();
        Wheel();
        Assert.True(page.VerticalOffset > 0);
    });

    private static Task OnSta(Action test)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() => { try { test(); completion.SetResult(); } catch (Exception ex) { completion.SetException(ex); } });
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        return completion.Task.WaitAsync(TimeSpan.FromSeconds(15));
    }
}
