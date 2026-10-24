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

    public ImageSource? Artwork { get => (ImageSource?)GetValue(ArtworkProperty); set => SetValue(ArtworkProperty, value); }
    public string Label { get => (string)GetValue(LabelProperty); set => SetValue(LabelProperty, value); }

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        var width = ActualWidth;
        var height = ActualHeight;
        if (width < 2 || height < 2) return;
        var radius = Math.Min(6, Math.Min(width, height) * .16);
        var bounds = new Rect(0, 0, width, height);
        dc.DrawRoundedRectangle(StatisticsDrawing.Brush("PosterTile"), null, bounds, radius, radius);
        if (Artwork is { Width: > 0, Height: > 0 } artwork)
        {
            var scale = Math.Max(width / artwork.Width, height / artwork.Height);
            dc.PushClip(new RectangleGeometry(bounds, radius, radius));
            dc.DrawImage(artwork, new Rect((width - artwork.Width * scale) / 2, (height - artwork.Height * scale) / 2,
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
        dc.DrawRoundedRectangle(null, new Pen(StatisticsDrawing.Brush("PosterBorder"), 1),
            new Rect(.5, .5, width - 1, height - 1), Math.Max(0, radius - .5), Math.Max(0, radius - .5));
    }
}
