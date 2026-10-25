using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Media;
using System.Xml.Linq;

namespace GameHelper.Tests;

public sealed class DesktopWeeklySummaryLayoutTests
{
    [Theory]
    [InlineData(220, "10.6")]
    [InlineData(252, "120.0")]
    [InlineData(220, "999.9")]
    public Task WeeklyHoursFitWithoutOverlappingChart(double width, string hours) => OnSta(() =>
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "GameHelper.sln"))) root = root.Parent;
        Assert.NotNull(root);
        var document = XDocument.Load(Path.Combine(root!.FullName, "GameHelper.Desktop", "MainWindow.xaml"));
        var fragment = new XElement(document.Descendants().First(element => element.Name.LocalName == "Grid"
            && element.Elements().Any(child => child.Name.LocalName == "PlaytimeMiniTrend")));
        foreach (var element in fragment.Descendants().Where(element => element.Name.NamespaceName == "clr-namespace:GameHelper.Desktop"))
            element.Name = XName.Get(element.Name.LocalName, "clr-namespace:GameHelper.Desktop;assembly=GameHelper.Desktop");
        fragment.SetAttributeValue(XNamespace.Xmlns + "local", "clr-namespace:GameHelper.Desktop;assembly=GameHelper.Desktop");
        var grid = (Grid)XamlReader.Parse(fragment.ToString());
        grid.DataContext = new { WeeklyHours = hours };
        grid.Measure(new Size(width, 100)); grid.Arrange(new Rect(0, 0, width, grid.DesiredSize.Height)); grid.UpdateLayout();
        var texts = Descendants(grid).OfType<TextBlock>().ToArray();
        Assert.Contains(hours, string.Concat(texts.Select(text => text.Text)));
        var chart = Descendants(grid).OfType<GameHelper.Desktop.PlaytimeMiniTrend>().Single();
        var chartLeft = chart.TranslatePoint(new Point(), grid).X;
        foreach (var text in texts)
        {
            var measured = new FormattedText(text.Text, CultureInfo.CurrentCulture, text.FlowDirection,
                new Typeface(text.FontFamily, text.FontStyle, text.FontWeight, text.FontStretch), text.FontSize, Brushes.Black, 1);
            Assert.True(text.TranslatePoint(new Point(), grid).X + measured.WidthIncludingTrailingWhitespace <= chartLeft - 4,
                $"{text.Text} needs {measured.WidthIncludingTrailingWhitespace:N1}px but chart starts at {chartLeft:N1}px");
        }
    });

    private static IEnumerable<DependencyObject> Descendants(DependencyObject parent)
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            yield return child;
            foreach (var descendant in Descendants(child)) yield return descendant;
        }
    }
    private static Task OnSta(Action test)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() => { try { test(); completion.SetResult(); } catch (Exception ex) { completion.SetException(ex); } });
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        return completion.Task.WaitAsync(TimeSpan.FromSeconds(15));
    }
}
