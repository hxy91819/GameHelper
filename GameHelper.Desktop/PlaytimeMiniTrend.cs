using System.Windows;
using System.Windows.Media;
using GameHelper.Core.Models;

namespace GameHelper.Desktop;

/// <summary>Compact real-history bars in the library's weekly summary.</summary>
public sealed class PlaytimeMiniTrend : FrameworkElement
{
    public static readonly DependencyProperty PointsProperty = DependencyProperty.Register(nameof(Points), typeof(IReadOnlyList<DashboardBucket>), typeof(PlaytimeMiniTrend),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));
    public IReadOnlyList<DashboardBucket>? Points { get => (IReadOnlyList<DashboardBucket>?)GetValue(PointsProperty); set => SetValue(PointsProperty, value); }
    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        if (Points is not { Count: > 0 } points) return;
        var max = Math.Max(1, points.Max(point => point.Minutes));
        var pitch = ActualWidth / points.Count;
        for (var index = 0; index < points.Count; index++)
        {
            var height = points[index].Minutes / (double)max * ActualHeight;
            dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromRgb(180, 164, 208)), null,
                new Rect(index * pitch, ActualHeight - height, Math.Max(1, pitch - 5), height), 3, 3);
        }
    }
}
