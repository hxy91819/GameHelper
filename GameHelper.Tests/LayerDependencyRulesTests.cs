using System.Xml.Linq;

namespace GameHelper.Tests;

public sealed class LayerDependencyRulesTests
{
    [Fact]
    public void CoreProject_ShouldNotReferenceShellProjects()
    {
        var solutionRoot = GetSolutionRoot();
        var coreProjectPath = Path.Combine(solutionRoot, "GameHelper.Core", "GameHelper.Core.csproj");
        var references = ReadProjectReferences(coreProjectPath);

        Assert.DoesNotContain(references, reference => reference.Contains("GameHelper.ConsoleHost", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(references, reference => reference.Contains("GameHelper.WinUI", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(references, reference => reference.Contains("GameHelper.Web", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void InfrastructureProject_ShouldOnlyReferenceCore()
    {
        var solutionRoot = GetSolutionRoot();
        var infrastructureProjectPath = Path.Combine(solutionRoot, "GameHelper.Infrastructure", "GameHelper.Infrastructure.csproj");
        var references = ReadProjectReferences(infrastructureProjectPath);

        Assert.All(references, reference => Assert.Contains("GameHelper.Core", reference, StringComparison.OrdinalIgnoreCase));
    }

    private static IReadOnlyList<string> ReadProjectReferences(string projectPath)
    {
        var document = XDocument.Load(projectPath);
        return document
            .Descendants("ProjectReference")
            .Select(node => node.Attribute("Include")?.Value ?? string.Empty)
            .ToList();
    }

    private static string GetSolutionRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "GameHelper.sln")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate GameHelper.sln from test base directory.");
    }
}
