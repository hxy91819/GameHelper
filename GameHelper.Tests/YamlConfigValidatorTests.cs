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

    [Theory]
    [InlineData("hdr: true", false)]
    [InlineData("hdr: {allowedDisplays: TV}", false)]
    [InlineData("hdr: {disabledDisplays: [null]}", false)]
    [InlineData("hdr: {allowedDisplays: [{}]}", false)]
    [InlineData("hdr: {allowedDisplays: [], disabledDisplays: [desk]}", true)]
    [InlineData("hdr: {allowedDisplays: [TV], disabledDisplays: [TV]}", true)]
    public void Validate_HdrDisplaySelection_RequiresListsOfNonEmptyNames(string yaml, bool valid)
    {
        File.WriteAllText(_configPath, yaml);
        Assert.Equal(valid, YamlConfigValidator.Validate(_configPath).IsValid);
    }

    [Theory]
    [InlineData("monitor: Invalid", "monitor must be ETW or WMI.")]
    [InlineData("startup: { launchOnStartup: maybe }", "startup.launchOnStartup must be a boolean.")]
    [InlineData("startup: { autoStartMonitor: [true] }", "startup.autoStartMonitor must be a boolean.")]
    [InlineData("startup: true", "startup must be a mapping (object).")]
    public void Validate_PersistedGlobalKeys_RejectInvalidValues(string yaml, string expectedError)
    {
        File.WriteAllText(_configPath, yaml);
        var result = YamlConfigValidator.Validate(_configPath);
        Assert.False(result.IsValid);
        Assert.Contains(expectedError, result.Errors);
    }

    [Theory]
    [InlineData("monitor: ETW")]
    [InlineData("monitor: WMI")]
    [InlineData("startup: { autoStartMonitor: true, launchOnStartup: false }")]
    [InlineData("monitor: WMI\nstartup: { autoStartMonitor: false, launchOnStartup: true }")]
    public void Validate_PersistedGlobalKeys_AcceptsValidValues(string yaml)
    {
        File.WriteAllText(_configPath, yaml);
        Assert.True(YamlConfigValidator.Validate(_configPath).IsValid);
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

    [Theory]
    [InlineData("2.5", true)]
    [InlineData("0", false)]
    [InlineData("fast", false)]
    public void Validate_GameSpeed_RequiresPositiveNumber(string speed, bool expectedValid)
    {
        File.WriteAllText(_configPath, $$"""
monitor: ETW
games:
  - dataKey: game
    executable: game.exe
    speed: {{speed}}
""");

        var result = YamlConfigValidator.Validate(_configPath);

        Assert.Equal(expectedValid, result.IsValid);
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
