using System.Xml.Linq;

namespace GameHelper.Tests;

public sealed class DesktopBrandTests
{
    [Fact]
    public void BrandVector_KeepsExactSvgPathsColorsAnd64UnitCanvas()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "GameHelper.sln"))) directory = directory.Parent;
        Assert.NotNull(directory);
        var assets = Path.Combine(directory!.FullName, "GameHelper.Desktop", "Assets");
        var svg = XDocument.Load(Path.Combine(assets, "gamehelper-logo.svg"));
        var xaml = XDocument.Load(Path.Combine(assets, "Brand.xaml"));
        Assert.Equal("0 0 64 64", svg.Root!.Attribute("viewBox")!.Value);
        var paths = svg.Root.Elements().Where(element => element.Name.LocalName == "path").ToArray();
        var geometry = xaml.Root!.Elements().Where(element => element.Name.LocalName == "Geometry").ToArray();
        Assert.Equal(paths.Select(path => path.Attribute("d")!.Value), geometry.Select(element => element.Value));
        Assert.Equal("currentColor", paths[0].Attribute("fill")!.Value);
        Assert.Equal("#ac94d7", paths[1].Attribute("fill")!.Value);
        var drawings = xaml.Descendants().Where(element => element.Name.LocalName == "GeometryDrawing").ToArray();
        Assert.Contains(drawings, element => element.Attribute("Brush")?.Value == "Transparent" && element.Attribute("Geometry")?.Value == "M0,0 H64 V64 H0 Z");
        Assert.Contains(drawings, element => element.Attribute("Brush")?.Value == "#202020");
        Assert.Contains(drawings, element => element.Attribute("Brush")?.Value == "#AC94D7");
    }
}
