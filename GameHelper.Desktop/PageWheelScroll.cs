using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace GameHelper.Desktop;

/// <summary>Gives the page ownership of vertical wheel input over nested item controls.</summary>
public static class PageWheelScroll
{
    public static readonly DependencyProperty EnabledProperty = DependencyProperty.RegisterAttached(
        "Enabled", typeof(bool), typeof(PageWheelScroll), new PropertyMetadata(false, OnChanged));
    public static bool GetEnabled(DependencyObject target) => (bool)target.GetValue(EnabledProperty);
    public static void SetEnabled(DependencyObject target, bool value) => target.SetValue(EnabledProperty, value);
    private static void OnChanged(DependencyObject target, DependencyPropertyChangedEventArgs args)
    {
        if (target is not ScrollViewer page) return;
        if ((bool)args.OldValue) page.PreviewMouseWheel -= OnWheel;
        if ((bool)args.NewValue) page.PreviewMouseWheel += OnWheel;
    }

    private static void OnWheel(object sender, MouseWheelEventArgs args)
    {
        var page = (ScrollViewer)sender;
        var lines = SystemParameters.WheelScrollLines;
        if (lines == 0) return;
        var step = lines < 0 ? page.ViewportHeight : lines * 16d;
        page.ScrollToVerticalOffset(page.VerticalOffset - args.Delta / 120d * step);
        args.Handled = true;
    }
}
