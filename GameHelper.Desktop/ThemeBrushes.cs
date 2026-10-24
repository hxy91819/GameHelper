using System.Windows;
using System.Windows.Media;

namespace GameHelper.Desktop;

/// <summary>Semantic palette access for native dialogs and custom drawing; tests without an App use dark defaults.</summary>
internal static class ThemeBrushes
{
    private static readonly IReadOnlyDictionary<string, string> Defaults = new Dictionary<string, string>
    {
        ["Page"] = "#101413",
        ["Sidebar"] = "#141A18",
        ["Surface"] = "#19211E",
        ["Input"] = "#111714",
        ["ControlSurface"] = "#25312B",
        ["Border"] = "#35463C",
        ["PanelBorder"] = "#2B3630",
        ["StrongBorder"] = "#56745F",
        ["TextPrimary"] = "#F0F2EB",
        ["Muted"] = "#A4B2A9",
        ["Accent"] = "#B8E8CE",
        ["AccentText"] = "#122219",
        ["SelectedSurface"] = "#355744",
        ["NavigationSelected"] = "#2A4033",
        ["Selection"] = "#253A2E",
        ["Hover"] = "#202D25",
        ["Focus"] = "#C7F1D9",
        ["FocusStrong"] = "#D3F4E2",
        ["HoverBorder"] = "#8CB69C",
        ["ScrollThumb"] = "#4D6255",
        ["TextSecondary"] = "#B7C4BB",
        ["Success"] = "#7ED99A",
        ["Warning"] = "#F0C783",
        ["Danger"] = "#45303B",
        ["TooltipSurface"] = "#25352B",
        ["ChartGrid"] = "#2B3930",
        ["ChartRecent"] = "#7EDDB5",
        ["ChartBarEnd"] = "#477B5B",
        ["CalendarZero"] = "#26332B",
        ["CalendarOne"] = "#285B55",
        ["CalendarTwo"] = "#388875",
        ["CalendarThree"] = "#53B594",
        ["CalendarFour"] = "#91E5BC",
        ["CalendarFocus"] = "#D9FFF1",
        ["RingTrack"] = "#304036",
        ["TableHover"] = "#293D30",
        ["TableSelected"] = "#304D3C",
        ["TableAlternate"] = "#1D2821",
        ["MetricStart"] = "#2D493A",
        ["MetricEnd"] = "#1D3025",
        ["MetricLabel"] = "#D7E8DD",
        ["PosterStart"] = "#35483D",
        ["PosterEnd"] = "#1A241E",
        ["PosterTile"] = "#223146",
        ["PosterLetter"] = "#8FA6C6",
        ["PosterBorder"] = "#3A4C66",
        ["RowIcon"] = "#30483A",
        ["MetricOther"] = "#D3D5B8",
    };

    internal static Brush Get(string key)
    {
        if (Application.Current?.TryFindResource(key) is Brush brush) return brush;
        var fallback = new SolidColorBrush((Color)ColorConverter.ConvertFromString(Defaults[key]));
        fallback.Freeze();
        return fallback;
    }

    internal static Color Color(string key) => ((SolidColorBrush)Get(key)).Color;
}
