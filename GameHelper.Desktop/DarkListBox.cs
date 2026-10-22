using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;

namespace GameHelper.Desktop;

/// <summary>Keeps the explicit dark surface when a network operation temporarily disables a list.</summary>
internal sealed class DarkListBox : ListBox
{
    internal DarkListBox()
    {
        var border = new FrameworkElementFactory(typeof(Border));
        foreach (var pair in new[] { (Border.BackgroundProperty, nameof(Background)), (Border.BorderBrushProperty, nameof(BorderBrush)), (Border.BorderThicknessProperty, nameof(BorderThickness)) })
            border.SetBinding(pair.Item1, new Binding(pair.Item2) { RelativeSource = RelativeSource.TemplatedParent });
        var scroll = new FrameworkElementFactory(typeof(ScrollViewer));
        scroll.SetValue(ScrollViewer.CanContentScrollProperty, true);
        scroll.SetValue(ScrollViewer.HorizontalScrollBarVisibilityProperty, ScrollBarVisibility.Disabled);
        scroll.SetValue(ScrollViewer.VerticalScrollBarVisibilityProperty, ScrollBarVisibility.Auto);
        scroll.SetValue(FocusableProperty, false);
        scroll.AppendChild(new FrameworkElementFactory(typeof(ItemsPresenter))); border.AppendChild(scroll);
        Template = new ControlTemplate(typeof(ListBox)) { VisualTree = border };
    }
}
