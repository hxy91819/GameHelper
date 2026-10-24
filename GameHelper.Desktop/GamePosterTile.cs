using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace GameHelper.Desktop;

/// <summary>Rounded game poster with a lettered fallback; sizes are set by the host layout.</summary>
public sealed class GamePosterTile : FrameworkElement
{
    public static readonly DependencyProperty ArtworkProperty = DependencyProperty.Register(nameof(Artwork), typeof(ImageSource),
        typeof(GamePosterTile), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty LabelProperty = DependencyProperty.Register(nameof(Label), typeof(string),
        typeof(GamePosterTile), new FrameworkPropertyMetadata(string.Empty, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty RadiusProperty = DependencyProperty.Register(nameof(Radius), typeof(double), typeof(GamePosterTile),
        new FrameworkPropertyMetadata(6d, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty RoundBottomProperty = DependencyProperty.Register(nameof(RoundBottom), typeof(bool), typeof(GamePosterTile),
        new FrameworkPropertyMetadata(true, FrameworkPropertyMetadataOptions.AffectsRender));
    public bool RoundBottom { get => (bool)GetValue(RoundBottomProperty); set => SetValue(RoundBottomProperty, value); }
    public static readonly DependencyProperty CropYProperty = DependencyProperty.Register(nameof(CropY), typeof(double), typeof(GamePosterTile), new FrameworkPropertyMetadata(.5d, FrameworkPropertyMetadataOptions.AffectsRender));
    public double CropY { get => (double)GetValue(CropYProperty); set => SetValue(CropYProperty, value); }
    public double Radius { get => (double)GetValue(RadiusProperty); set => SetValue(RadiusProperty, value); }

    public ImageSource? Artwork { get => (ImageSource?)GetValue(ArtworkProperty); set => SetValue(ArtworkProperty, value); }
    public string Label { get => (string)GetValue(LabelProperty); set => SetValue(LabelProperty, value); }

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        var width = ActualWidth;
        var height = ActualHeight;
        if (width < 2 || height < 2) return;
        var radius = Math.Min(Radius, Math.Min(width, height) * .5);
        var bounds = new Rect(0, 0, width, height);
        Geometry clip = new RectangleGeometry(bounds, radius, radius);
        if (!RoundBottom)
        {
            var shape = new StreamGeometry();
            using (var context = shape.Open())
            {
                context.BeginFigure(new Point(radius, 0), true, true);
                context.LineTo(new Point(width - radius, 0), true, false);
                context.ArcTo(new Point(width, radius), new Size(radius, radius), 0, false, SweepDirection.Clockwise, true, false);
                context.LineTo(new Point(width, height), true, false); context.LineTo(new Point(0, height), true, false);
                context.LineTo(new Point(0, radius), true, false);
                context.ArcTo(new Point(radius, 0), new Size(radius, radius), 0, false, SweepDirection.Clockwise, true, false);
            }
            clip = shape;
        }
        dc.PushClip(clip);
        dc.DrawRectangle(StatisticsDrawing.Brush("PosterTile"), null, bounds);
        if (Artwork is { Width: > 0, Height: > 0 } artwork)
        {
            var scale = Math.Max(width / artwork.Width, height / artwork.Height);
            dc.PushClip(clip);
            dc.DrawImage(artwork, new Rect((width - artwork.Width * scale) / 2, (height - artwork.Height * scale) * Math.Clamp(CropY, 0, 1),
                artwork.Width * scale, artwork.Height * scale));
            dc.Pop();
        }
        else if (!string.IsNullOrWhiteSpace(Label))
        {
            var text = new FormattedText(Label, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
                new Typeface("Segoe UI"), Math.Round(Math.Min(width, height) * .42), StatisticsDrawing.Brush("PosterLetter"),
                VisualTreeHelper.GetDpi(this).PixelsPerDip);
            dc.DrawText(text, new Point((width - text.Width) / 2, (height - text.Height) / 2));
        }
        dc.Pop();

    }
}
