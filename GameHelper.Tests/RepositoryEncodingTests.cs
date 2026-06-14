using System.Text;

namespace GameHelper.Tests;

public sealed class RepositoryEncodingTests
{
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    private static readonly HashSet<string> TextExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".cs",
        ".csproj",
        ".props",
        ".targets",
        ".xaml",
        ".xml",
        ".md",
        ".yml",
        ".yaml",
        ".json",
        ".ps1",
        ".sh",
        ".csv",
        ".sln",
    };

    private static readonly HashSet<string> TextFileNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ".editorconfig",
        ".gitattributes",
        "AGENTS.md",
        "README.md",
    };

    [Fact]
    public void RepositoryTextFiles_ShouldDecodeAsUtf8()
    {
        var solutionRoot = GetSolutionRoot();
        var invalidFiles = EnumerateRepositoryTextFiles(solutionRoot)
            .Where(file => !CanDecodeAsUtf8(file))
            .Select(file => Path.GetRelativePath(solutionRoot, file))
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        Assert.Empty(invalidFiles);
    }

    [Fact]
    public void EncodingConfiguration_ShouldRequireUtf8()
    {
        var solutionRoot = GetSolutionRoot();
        var editorConfig = File.ReadAllText(Path.Combine(solutionRoot, ".editorconfig"), StrictUtf8);
        var gitAttributes = File.ReadAllText(Path.Combine(solutionRoot, ".gitattributes"), StrictUtf8);

        Assert.Contains("charset = utf-8", editorConfig, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("working-tree-encoding=UTF-8", gitAttributes, StringComparison.Ordinal);
    }

    private static IEnumerable<string> EnumerateRepositoryTextFiles(string solutionRoot)
    {
        return Directory.EnumerateFiles(solutionRoot, "*", SearchOption.AllDirectories)
            .Where(file => !IsIgnoredPath(solutionRoot, file))
            .Where(IsKnownTextFile);
    }

    private static bool CanDecodeAsUtf8(string file)
    {
        try
        {
            _ = StrictUtf8.GetString(File.ReadAllBytes(file));
            return true;
        }
        catch (DecoderFallbackException)
        {
            return false;
        }
    }

    private static bool IsIgnoredPath(string solutionRoot, string file)
    {
        var relativePath = Path.GetRelativePath(solutionRoot, file);
        var separators = new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar };
        var parts = relativePath.Split(separators, StringSplitOptions.RemoveEmptyEntries);

        return parts.Any(part =>
            part.Equals(".git", StringComparison.OrdinalIgnoreCase) ||
            part.Equals(".vs", StringComparison.OrdinalIgnoreCase) ||
            part.Equals("bin", StringComparison.OrdinalIgnoreCase) ||
            part.Equals("obj", StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsKnownTextFile(string file)
    {
        var fileName = Path.GetFileName(file);
        return TextFileNames.Contains(fileName) || TextExtensions.Contains(Path.GetExtension(file));
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
