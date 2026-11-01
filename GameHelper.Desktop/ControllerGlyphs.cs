using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using GameHelper.Desktop.Input;

namespace GameHelper.Desktop;

internal enum ControllerButton
{
    Confirm, Back, Auxiliary, Search, Menu, ModeModifier,
    Directions, Horizontal, Vertical, LeftShoulder, RightShoulder, LeftTrigger, RightTrigger
}

/// <summary>Local vector button artwork; face symbols never depend on installed fonts.</summary>
internal static class ControllerGlyphs
{
    // Frozen drawings can be shared by windows and dispatchers. UI elements cannot.
    private static readonly IReadOnlyDictionary<(bool Ps, ControllerButton Button), DrawingImage> Drawings =
        (from ps in new[] { false, true }
         from button in Enum.GetValues<ControllerButton>()
         select (ps, button)).ToDictionary(key => key, key => Draw(key.ps, key.button));

    internal static Image Create(ControllerKind kind, ControllerButton button, double size = 28)
    {
        var image = new Image { Source = Drawings[(kind == ControllerKind.PlayStation, button)],
            Width = size, Height = size, Stretch = Stretch.Uniform, VerticalAlignment = VerticalAlignment.Center };
        var name = Name(kind, button);
        image.ToolTip = name;
        AutomationProperties.SetName(image, name);
        return image;
    }

    internal static string Name(ControllerKind kind, ControllerButton button) => button switch
    {
        ControllerButton.Confirm => kind == ControllerKind.PlayStation ? "交叉键" : "A",
        ControllerButton.Back => kind == ControllerKind.PlayStation ? "圆圈键" : "B",
        ControllerButton.Auxiliary => kind == ControllerKind.PlayStation ? "方块键" : "X",
        ControllerButton.Search => kind == ControllerKind.PlayStation ? "三角键" : "Y",
        ControllerButton.Menu => kind == ControllerKind.PlayStation ? "Options" : "Menu / Start",
        ControllerButton.ModeModifier => kind == ControllerKind.PlayStation ? "Share / Create" : "View / Back",
        ControllerButton.LeftShoulder => kind == ControllerKind.PlayStation ? "L1" : "LB",
        ControllerButton.RightShoulder => kind == ControllerKind.PlayStation ? "R1" : "RB",
        ControllerButton.LeftTrigger => kind == ControllerKind.PlayStation ? "L2" : "LT",
        ControllerButton.RightTrigger => kind == ControllerKind.PlayStation ? "R2" : "RT",
        ControllerButton.Horizontal => "方向键左右 / 左摇杆左右",
        ControllerButton.Vertical => "方向键上下 / 左摇杆上下",
        _ => "方向键 / 左摇杆"
    };

    private static DrawingImage Draw(bool ps, ControllerButton button)
    {
        var group = new DrawingGroup();
        using (var dc = group.Open())
        {
            dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, 32, 32));
            var ink = ConsoleModeVisuals.Text;
            var pen = new Pen(ink, 1.8) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round };
            void Shape(string path) => dc.DrawGeometry(null, pen, Geometry.Parse(path));
            if (button is ControllerButton.Confirm or ControllerButton.Back or ControllerButton.Auxiliary or ControllerButton.Search)
            {
                dc.DrawEllipse(ConsoleModeVisuals.Raised, new Pen(ConsoleModeVisuals.Secondary, 1.2), new Point(16, 16), 14, 14);
                if (!ps)
                    Letter(button switch { ControllerButton.Confirm => "A", ControllerButton.Back => "B", ControllerButton.Auxiliary => "X", _ => "Y" }, 17);
                else switch (button)
                {
                    case ControllerButton.Confirm: Shape("M11,11 L21,21 M21,11 L11,21"); break;
                    case ControllerButton.Back: dc.DrawEllipse(null, pen, new Point(16, 16), 7, 7); break;
                    case ControllerButton.Auxiliary: dc.DrawRectangle(null, pen, new Rect(9, 9, 14, 14)); break;
                    case ControllerButton.Search: Shape("M16,8 L24,22 H8 Z"); break;
                }
            }
            else if (button is ControllerButton.LeftShoulder or ControllerButton.RightShoulder or ControllerButton.LeftTrigger or ControllerButton.RightTrigger)
            {
                var trigger = button is ControllerButton.LeftTrigger or ControllerButton.RightTrigger;
                dc.DrawGeometry(ConsoleModeVisuals.Raised, new Pen(ConsoleModeVisuals.Secondary, 1.2),
                    Geometry.Parse(trigger ? "M7,3 H25 Q29,3 29,9 L27,27 Q27,29 23,29 H9 Q5,29 5,25 L3,9 Q3,3 7,3 Z" : "M6,6 H26 Q29,6 29,10 V23 Q29,26 26,26 H6 Q3,26 3,23 V10 Q3,6 6,6 Z"));
                Letter(Name(ps ? ControllerKind.PlayStation : ControllerKind.Xbox, button), 13);
            }
            else if (button is ControllerButton.Menu or ControllerButton.ModeModifier)
            {
                dc.DrawRoundedRectangle(ConsoleModeVisuals.Raised, new Pen(ConsoleModeVisuals.Secondary, 1.2), new Rect(3, 4, 26, 24), 7, 7);
                if (button == ControllerButton.Menu) Shape("M10,11 H22 M10,16 H22 M10,21 H22");
                else if (ps) Shape("M10,11 L7,8 M16,10 V6 M22,11 L25,8 M12,16 H20 V23 H12 Z");
                else Shape("M8,10 H19 V19 H8 Z M13,14 H24 V23 H13");
            }
            else
            {
                Shape("M12,3 H20 V12 H29 V20 H20 V29 H12 V20 H3 V12 H12 Z");
                if (button != ControllerButton.Vertical) Shape("M8,14 L6,16 L8,18 M24,14 L26,16 L24,18");
                if (button != ControllerButton.Horizontal) Shape("M14,8 L16,6 L18,8 M14,24 L16,26 L18,24");
            }
            void Letter(string text, double size)
            {
                var formatted = new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                    new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal), size, ink, 1);
                dc.DrawText(formatted, new Point((32 - formatted.Width) / 2, (32 - formatted.Height) / 2));
            }
        }
        group.Freeze();
        var image = new DrawingImage(group); image.Freeze(); return image;
    }
}

internal sealed record ControllerHint(string Label, params ControllerButton[] Buttons)
{
    internal bool Chord { get; init; }
}

/// <summary>Keeps each icon and its action together when television/small-window hints wrap.</summary>
internal sealed class ControllerHintBar : WrapPanel
{
    private ControllerKind? previousKind;
    private ControllerHint[] previousHints = [];
    private double previousSize;
    internal double FontSize { get; set; } = 16;
    internal Brush Foreground { get; set; } = ConsoleModeVisuals.Text;

    internal void Set(ControllerKind kind, params ControllerHint[] hints)
    {
        // Input polls at 60 Hz; unchanged hints retain their visual tree.
        if (previousKind == kind && previousSize == FontSize && previousHints.Length == hints.Length &&
            previousHints.Zip(hints).All(pair => pair.First.Label == pair.Second.Label && pair.First.Chord == pair.Second.Chord &&
                pair.First.Buttons.SequenceEqual(pair.Second.Buttons))) return;
        previousKind = kind; previousHints = hints; previousSize = FontSize;
        Children.Clear();
        foreach (var hint in hints)
        {
            var group = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 20, 6) };
            for (var index = 0; index < hint.Buttons.Length; index++)
            {
                if (index > 0) group.Children.Add(Text(hint.Chord ? "+" : "/", 4));
                group.Children.Add(ControllerGlyphs.Create(kind, hint.Buttons[index], FontSize + 10));
            }
            group.Children.Add(Text(hint.Label, 7));
            AutomationProperties.SetName(group, string.Join(hint.Chord ? " 加 " : " 或 ", hint.Buttons.Select(button => ControllerGlyphs.Name(kind, button))) + "：" + hint.Label);
            Children.Add(group);
        }
        TextBlock Text(string value, double gap) => new() { Text = value, FontSize = FontSize, Foreground = Foreground,
            Margin = new Thickness(gap, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
    }
}
