using System.Text;

namespace GameHelper.Tests;

public sealed class RepositoryEncodingTests
{
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    private static readonly string[] MojibakeMarkers =
    {
        "\u95b0\u5d87\u7586", // 配置
        "\u6d5c\u6391\u59e9", // 互动
        "\u7039\u70b4\u6902\u9429\u621e\u5e36", // 实时监控
        "\u5a13\u544a\u5799\u93b0\u590a\u63e9", // 游戏愉快
        "\u7487\ufe3d\u510f", // 详情
        "\u93c2\u56e6\u6b22", // 文件
        "\u93c3\u30e5\u7e54", // 日志
        "\u9429\u621e\u5e36", // 监控
        "\u7ee0\uff04\u608a", // 管理
        "\u934f\u3125\u772c", // 全局
        "\ufffd",
    };

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
    public void RepositoryTextFiles_ShouldNotContainCommonMojibakeMarkers()
    {
        var solutionRoot = GetSolutionRoot();
        var corruptedFiles = EnumerateRepositoryTextFiles(solutionRoot)
            .Select(file => new
            {
                File = file,
                Content = File.ReadAllText(file, StrictUtf8)
            })
            .Where(item => ContainsMojibakeMarker(item.Content))
            .Select(item => Path.GetRelativePath(solutionRoot, item.File))
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        Assert.Empty(corruptedFiles);
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

    private static bool ContainsMojibakeMarker(string content)
    {
        return MojibakeMarkers.Any(marker => content.Contains(marker, StringComparison.Ordinal));
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
