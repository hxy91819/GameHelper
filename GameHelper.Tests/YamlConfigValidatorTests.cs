using GameHelper.Infrastructure.Validators;

namespace GameHelper.Tests;

public class YamlConfigValidatorTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _configPath;

    public YamlConfigValidatorTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "GameHelperTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _configPath = Path.Combine(_tempDir, "config.yml");
    }

    [Fact]
    public void Validate_WhenYamlContainsUnquotedColon_ReturnsActionableError()
    {
        File.WriteAllText(_configPath, """
monitor: ETW
games:
  - dataKey: granblue
    executable: granblue.exe
    displayName: Granblue Fantasy: Relink
""");

        var result = YamlConfigValidator.Validate(_configPath);

        Assert.False(result.IsValid);
        var error = Assert.Single(result.Errors);
        Assert.Contains("Invalid YAML", error);
        Assert.Contains("line", error, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("quote string values", error, StringComparison.OrdinalIgnoreCase);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDir))
            {
                Directory.Delete(_tempDir, recursive: true);
            }
        }
        catch
        {
            // best-effort cleanup
        }
    }
}
