using GameHelper.Core.Models;
using GameHelper.Infrastructure.Providers;

namespace GameHelper.Tests;

public class JsonConfigProviderTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _configPath;

    public JsonConfigProviderTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "GameHelperTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _configPath = Path.Combine(_tempDir, "config.json");
    }

    [Fact]
    public void Load_WhenFileMissing_ReturnsEmpty()
    {
        var provider = new JsonConfigProvider(_configPath);

        var map = provider.Load();

        Assert.NotNull(map);
        Assert.Empty(map);
    }

    [Fact]
    public void Save_Then_Load_Roundtrip_UsesDataKeyAsStorageKey()
    {
        var provider = new JsonConfigProvider(_configPath);
        var input = new Dictionary<string, GameConfig>(StringComparer.OrdinalIgnoreCase)
        {
            ["a"] = new()
            {
                DataKey = "cyberpunk2077",
                ExecutableName = "cyberpunk2077.exe",
                IsEnabled = true,
                HdrEnabled = true
            },
            ["b"] = new()
            {
                DataKey = "rdr2",
                ExecutableName = "rdr2.exe",
                IsEnabled = false,
                HdrEnabled = false
            }
        };

        provider.Save(input);

        var output = provider.Load();
        Assert.Equal(2, output.Count);
        Assert.Contains("cyberpunk2077", output.Keys);
        Assert.Contains("rdr2", output.Keys);
        Assert.Contains(output.Values, v => v.DataKey == "cyberpunk2077" && v.Executable == "cyberpunk2077.exe");
        Assert.Contains(output.Values, v => v.DataKey == "rdr2" && v.Executable == "rdr2.exe");
    }

    [Fact]
    public void Load_ArrayOfStrings_IsNotSupported()
    {
        File.WriteAllText(_configPath, """
{
  "games": [
    "witcher3.exe",
    "forza_horizon_5.exe"
  ]
}
""");
        var provider = new JsonConfigProvider(_configPath);

        var map = provider.Load();

        Assert.Empty(map);
    }

    [Fact]
    public void Load_NewFormatMissingDataKey_ThrowsInvalidDataException()
    {
        File.WriteAllText(_configPath, """
{
  "games": [
    { "executable": "broken.exe" }
  ]
}
""");
        var provider = new JsonConfigProvider(_configPath);

        var exception = Assert.Throws<InvalidDataException>(() => provider.Load());
        Assert.Contains("DataKey", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Save_WhenDataKeyMissing_ThrowsInvalidDataException()
    {
        var provider = new JsonConfigProvider(_configPath);
        var input = new Dictionary<string, GameConfig>(StringComparer.OrdinalIgnoreCase)
        {
            ["sample.exe"] = new()
            {
                ExecutableName = "sample.exe"
            }
        };

        var exception = Assert.Throws<InvalidDataException>(() => provider.Save(input));
        Assert.Contains("DataKey", exception.Message, StringComparison.OrdinalIgnoreCase);
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
