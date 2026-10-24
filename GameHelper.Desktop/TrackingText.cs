using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace GameHelper.Desktop;

/// <summary>Native text with the explicit letter spacing used by the Studio eyebrow labels.</summary>
public sealed class TrackingText : Control
{
    public static readonly DependencyProperty TextProperty = DependencyProperty.Register(nameof(Text), typeof(string), typeof(TrackingText), new FrameworkPropertyMetadata("", FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty TrackingProperty = DependencyProperty.Register(nameof(Tracking), typeof(double), typeof(TrackingText), new FrameworkPropertyMetadata(1.8d, FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender));
    public string Text { get => (string)GetValue(TextProperty); set => SetValue(TextProperty, value); }
    public double Tracking { get => (double)GetValue(TrackingProperty); set => SetValue(TrackingProperty, value); }
    private FormattedText Letter(string letter) => new(letter, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
        new Typeface(FontFamily, FontStyle, FontWeight, FontStretch), FontSize, Foreground, VisualTreeHelper.GetDpi(this).PixelsPerDip);
    private IEnumerable<FormattedText> Letters()
    {
        var iterator = StringInfo.GetTextElementEnumerator(Text ?? "");
        while (iterator.MoveNext()) yield return Letter(iterator.GetTextElement());
    }
    protected override Size MeasureOverride(Size constraint)
    {
        var letters = Letters().ToArray();
        return letters.Length == 0 ? new Size() : new Size(letters.Sum(letter => letter.WidthIncludingTrailingWhitespace) + Math.Max(0, letters.Length - 1) * Tracking, letters.Max(letter => letter.Height));
    }
    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        double x = 0;
        foreach (var letter in Letters()) { dc.DrawText(letter, new Point(x, 0)); x += letter.WidthIncludingTrailingWhitespace + Tracking; }
    }
}
