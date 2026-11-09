using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Media;
using System.Xml.Linq;
using GameHelper.Core.Models;
using GameHelper.Desktop;

namespace GameHelper.Tests;

public sealed class DesktopGameLibraryLayoutTests
{
    // GameList.ActualWidth measured from the published WPF window at the three
    // Studio widths (1100 narrow layout, 1360 default, 1920 wide).
    public static TheoryData<int, double> StudioWindows => new()
    {
        { 1100, 638 },
        { 1360, 800 },
        { 1920, 1360 }
    };

    [Theory]
    [MemberData(nameof(StudioWindows))]
    public Task CardGridKeepsPosterSizeAndWrapsIntoRealColumns(int windowWidth, double gameListWidth) => OnStaAsync(() =>
    {
        var metrics = Measure(gameListWidth, itemCount: 13);
        Assert.Equal(164, metrics.PosterWidth, 1);
        Assert.Equal(246, metrics.PosterHeight, 1);
        Assert.Equal(180, metrics.ItemWidth, 1);
        Assert.Equal(2d / 3d, metrics.PosterWidth / metrics.PosterHeight, 3);
        Assert.Equal(MainWindow.CardColumnsFor(gameListWidth), metrics.FirstRowColumns);
        Assert.All(metrics.Rows, row => Assert.InRange(row, 1, metrics.FirstRowColumns));
        Assert.True(metrics.FirstRowColumns >= 3, $"{windowWidth}px window produced {metrics.FirstRowColumns} columns");
    });

    [Fact]
    public Task ExactSlotBoundaryKeepsFourColumnsAtThe1280Viewport() => OnStaAsync(() =>
    {
        var metrics = Measure(listWidth: 720, itemCount: 13);
        Assert.Equal(4, MainWindow.CardColumnsFor(720));
        Assert.Equal(4, metrics.FirstRowColumns);
        Assert.Equal(164, metrics.PosterWidth, 1);
    });

    [Fact]
    public Task WideWindowsAddColumnsAndLastRowKeepsPosterWidth() => OnStaAsync(() =>
    {
        var narrow = Measure(listWidth: 638, itemCount: 13);
        var standard = Measure(listWidth: 800, itemCount: 13);
        var wide = Measure(listWidth: 1360, itemCount: 13);
        Assert.True(narrow.FirstRowColumns < standard.FirstRowColumns,
            $"narrow={narrow.FirstRowColumns}, standard={standard.FirstRowColumns}");
        Assert.True(standard.FirstRowColumns < wide.FirstRowColumns,
            $"standard={standard.FirstRowColumns}, wide={wide.FirstRowColumns}");

        foreach (var metrics in new[] { narrow, standard, wide })
        {
            Assert.All(metrics.Posters, poster => Assert.Equal(164, poster.Width, 1));
            Assert.All(metrics.Posters, poster => Assert.Equal(246, poster.Height, 1));
            Assert.All(metrics.Posters, poster => Assert.True(poster.Right <= metrics.ListWidth + .5,
                $"poster right edge {poster.Right} exceeds list width {metrics.ListWidth}"));
            var lastRowStart = metrics.Posters.Length - metrics.Rows[^1];
            Assert.Equal(metrics.Posters[0].Left, metrics.Posters[lastRowStart].Left, 1);
        }
    });

    [Theory]
    [InlineData(1, 1)]
    [InlineData(179, 1)]
    [InlineData(180, 1)]
    [InlineData(359, 1)]
    [InlineData(360, 2)]
    [InlineData(540, 3)]
    [InlineData(638, 3)]
    [InlineData(720, 4)]
    [InlineData(800, 4)]
    [InlineData(1360, 7)]
    public void CardColumnsUseTheFixedCardSlot(double gameListWidth, int expected)
        => Assert.Equal(expected, MainWindow.CardColumnsFor(gameListWidth));

    private static LayoutMetrics Measure(double listWidth, int itemCount)
    {
        var resources = CardResources();
        var items = Enumerable.Range(0, itemCount)
            .Select(index => new LibraryGameItem(new LibraryGame
            {
                Config = new GameConfig { DataKey = "game-" + index, DisplayName = "Game " + index },
                Revision = "revision-" + index,
                Installation = new(GameInstallationState.Installed, "installed")
            }))
            .ToArray();
        var window = new LayoutHostWindow
        {
            Width = listWidth,
            Height = 900,
            WindowStyle = WindowStyle.None,
            ResizeMode = ResizeMode.NoResize,
            ShowActivated = false,
            ShowInTaskbar = false,
            Left = -32000,
            Top = -32000,
            Resources = resources
        };
        var list = new ListBox
        {
            ItemsSource = items,
            ItemTemplate = (DataTemplate)resources["CardTemplate"],
            ItemsPanel = (ItemsPanelTemplate)resources["CardsPanel"],
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(0),
            Width = listWidth,
            Height = 900
        };
        ScrollViewer.SetHorizontalScrollBarVisibility(list, ScrollBarVisibility.Disabled);
        ScrollViewer.SetVerticalScrollBarVisibility(list, ScrollBarVisibility.Disabled);
        window.Content = list;
        window.Show();
        window.UpdateLayout();
        list.UpdateLayout();
        try
        {
            var containers = Enumerable.Range(0, itemCount)
                .Select(index => Assert.IsType<ListBoxItem>(list.ItemContainerGenerator.ContainerFromIndex(index)))
                .ToArray();
            var rows = containers
                .GroupBy(container => Math.Round(container.TranslatePoint(new Point(), list).Y, 1))
                .OrderBy(row => row.Key)
                .Select(row => row.Count())
                .ToArray();
            var posters = containers
                .SelectMany(container => Descendants<GamePosterTile>(container))
                .Select(poster => new PosterBounds(
                    poster.TranslatePoint(new Point(), list).X,
                    poster.ActualWidth,
                    poster.ActualHeight))
                .ToArray();
            return new LayoutMetrics(listWidth, rows, containers[0].ActualWidth,
                posters[0].Width, posters[0].Height, posters);
        }
        finally { window.Close(); }
    }

    private static ResourceDictionary CardResources()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "GameHelper.sln"))) directory = directory.Parent;
        Assert.NotNull(directory);
        var document = XDocument.Load(Path.Combine(directory!.FullName, "GameHelper.Desktop", "MainWindow.xaml"));
        var x = XNamespace.Get("http://schemas.microsoft.com/winfx/2006/xaml");
        var presentation = XNamespace.Get("http://schemas.microsoft.com/winfx/2006/xaml/presentation");
        XElement Keyed(string type, string key) => new(document.Descendants().First(element =>
            element.Name.LocalName == type && (string?)element.Attribute(x + "Key") == key));
        var fragments = new[]
        {
            new XElement(presentation + "BooleanToVisibilityConverter", new XAttribute(x + "Key", "BoolVisibility")),
            Keyed("Style", "RunningHalo"),
            Keyed("DataTemplate", "CardTemplate"),
            Keyed("DataTemplate", "RowTemplate"),
            Keyed("ItemsPanelTemplate", "CardsPanel"),
            document.Descendants().First(element => element.Name.LocalName == "Style"
                && (string?)element.Attribute("TargetType") == "ListBoxItem")
        };
        const string localNamespace = "clr-namespace:GameHelper.Desktop";
        const string localAssemblyNamespace = localNamespace + ";assembly=GameHelper.Desktop";
        foreach (var element in fragments.SelectMany(fragment => new[] { fragment }.Concat(fragment.Descendants())))
        {
            if (element.Name.NamespaceName == localNamespace)
                element.Name = XName.Get(element.Name.LocalName, localAssemblyNamespace);
            element.Attributes().Where(attribute => attribute.Name.LocalName is "MouseDown"
                || attribute.IsNamespaceDeclaration && attribute.Name.LocalName == "local" && attribute.Value == localNamespace).Remove();
        }
        var dictionary = new XDocument(new XElement(presentation + "ResourceDictionary",
            new XAttribute(XNamespace.Xmlns + "x", x),
            new XAttribute(XNamespace.Xmlns + "local", "clr-namespace:GameHelper.Desktop;assembly=GameHelper.Desktop"),
            fragments));
        return (ResourceDictionary)XamlReader.Parse(dictionary.ToString());
    }

    private sealed record PosterBounds(double Left, double Width, double Height)
    {
        internal double Right => Left + Width;
    }

    private sealed record LayoutMetrics(double ListWidth, int[] Rows, double ItemWidth,
        double PosterWidth, double PosterHeight, PosterBounds[] Posters)
    {
        internal int FirstRowColumns => Rows[0];
    }

    private sealed class LayoutHostWindow : Window
    {
        public static readonly DependencyProperty CardPosterHeightProperty = DependencyProperty.Register(
            nameof(CardPosterHeight), typeof(double), typeof(LayoutHostWindow), new PropertyMetadata(MainWindow.CardHeight));
        public double CardPosterHeight { get => (double)GetValue(CardPosterHeightProperty); set => SetValue(CardPosterHeightProperty, value); }
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            if (child is T match) yield return match;
            foreach (var descendant in Descendants<T>(child)) yield return descendant;
        }
    }

    private static Task OnStaAsync(Action test)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() => { try { test(); completion.SetResult(); } catch (Exception ex) { completion.SetException(ex); } });
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        return completion.Task.WaitAsync(TimeSpan.FromSeconds(15));
    }
}
