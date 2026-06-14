using System.Text.RegularExpressions;

namespace GameHelper.Tests;

public sealed class DocumentationLinkTests
{
    private static readonly Regex MarkdownLinkPattern = new(
        @"(?<!!)\[[^\]]+\]\((?<target>[^)\s]+)(?:\s+""[^""]*"")?\)",
        RegexOptions.Compiled);

    [Fact]
    public void Docs_ShouldNotContainBrokenLocalMarkdownLinks()
    {
        var solutionRoot = GetSolutionRoot();
        var docsRoot = Path.Combine(solutionRoot, "docs");
        var markdownFiles = Directory.EnumerateFiles(docsRoot, "*.md", SearchOption.AllDirectories)
            .Append(Path.Combine(solutionRoot, "README.md"))
            .Where(File.Exists)
            .ToArray();

        var brokenLinks = new List<string>();

        foreach (var markdownFile in markdownFiles)
        {
            var content = File.ReadAllText(markdownFile);
            foreach (Match match in MarkdownLinkPattern.Matches(content))
            {
                var target = match.Groups["target"].Value;
                if (!TryResolveLocalTarget(markdownFile, target, out var resolvedPath))
                {
                    continue;
                }

                if (!File.Exists(resolvedPath) && !Directory.Exists(resolvedPath))
                {
                    brokenLinks.Add($"{Path.GetRelativePath(solutionRoot, markdownFile)} -> {target}");
                }
            }
        }

        Assert.Empty(brokenLinks);
    }

    private static bool TryResolveLocalTarget(string markdownFile, string target, out string resolvedPath)
    {
        resolvedPath = string.Empty;

        if (string.IsNullOrWhiteSpace(target) ||
            target.StartsWith("#", StringComparison.Ordinal) ||
            target.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            target.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ||
            target.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var pathOnly = target.Split('#', 2)[0];
        if (string.IsNullOrWhiteSpace(pathOnly))
        {
            return false;
        }

        pathOnly = Uri.UnescapeDataString(pathOnly).Replace('/', Path.DirectorySeparatorChar);
        resolvedPath = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(markdownFile)!, pathOnly));
        return true;
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
