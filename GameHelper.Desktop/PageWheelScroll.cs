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
    /// <summary>Allows a constrained details pane to scroll before forwarding at its edges to the page.</summary>
    public static readonly DependencyProperty IndependentProperty = DependencyProperty.RegisterAttached(
        "Independent", typeof(bool), typeof(PageWheelScroll), new PropertyMetadata(false));
    public static bool GetIndependent(DependencyObject target) => (bool)target.GetValue(IndependentProperty);
    public static void SetIndependent(DependencyObject target, bool value) => target.SetValue(IndependentProperty, value);

    private static void OnChanged(DependencyObject target, DependencyPropertyChangedEventArgs args)
    {
        if (target is not ScrollViewer page) return;
        if ((bool)args.OldValue) page.PreviewMouseWheel -= OnWheel;
        if ((bool)args.NewValue) page.PreviewMouseWheel += OnWheel;
    }

    private static void OnWheel(object sender, MouseWheelEventArgs args)
    {
        var page = (ScrollViewer)sender;
        for (var node = args.OriginalSource as DependencyObject; node is not null && node != page;)
        {
            if (node is ScrollViewer inner && GetIndependent(inner) && inner.ScrollableHeight > 0
                && (args.Delta < 0 ? inner.VerticalOffset < inner.ScrollableHeight : inner.VerticalOffset > 0))
            { page = inner; break; }
            node = node is System.Windows.Media.Visual or System.Windows.Media.Media3D.Visual3D
                ? System.Windows.Media.VisualTreeHelper.GetParent(node) : LogicalTreeHelper.GetParent(node);
        }
        var lines = SystemParameters.WheelScrollLines;
        if (lines == 0) return;
        var step = lines < 0 ? page.ViewportHeight : lines * 16d;
        page.ScrollToVerticalOffset(page.VerticalOffset - args.Delta / 120d * step);
        args.Handled = true;
    }
}
