using GameHelper.Core.Utilities;
using Xunit;

namespace GameHelper.Tests;

public sealed class ModLoaderDetectorTests : IDisposable
{
    private readonly string _tempDirectory = Path.Combine(
        Path.GetTempPath(),
        "GameHelperTests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public void Detect_WithNullPath_ReturnsNone()
    {
        Assert.Equal(ModLoaderKind.None, ModLoaderDetector.Detect(null).Kind);
        Assert.Null(ModLoaderDetector.Detect(null).LogPath);
    }

    [Fact]
    public void Detect_WithMissingDirectory_ReturnsNone()
    {
        var missing = Path.Combine(_tempDirectory, "does-not-exist", "Game.exe");
        Assert.Equal(ModLoaderKind.None, ModLoaderDetector.Detect(missing).Kind);
    }

    [Fact]
    public void Detect_WithPlainGameDirectory_ReturnsNone()
    {
        Directory.CreateDirectory(Path.Combine(_tempDirectory, "Game"));
        var exePath = Path.Combine(_tempDirectory, "Game", "Game.exe");
        File.WriteAllBytes(exePath, Array.Empty<byte>());

        var info = ModLoaderDetector.Detect(exePath);

        Assert.Equal(ModLoaderKind.None, info.Kind);
        Assert.Null(info.LogPath);
    }

    [Fact]
    public void Detect_WithBepInExFolder_ReturnsBepInExAndLogPath()
    {
        var gameDir = Path.Combine(_tempDirectory, "Game");
        Directory.CreateDirectory(Path.Combine(gameDir, "BepInEx"));
        var exePath = Path.Combine(gameDir, "Game.exe");
        File.WriteAllBytes(exePath, Array.Empty<byte>());

        var info = ModLoaderDetector.Detect(exePath);

        Assert.Equal(ModLoaderKind.BepInEx, info.Kind);
        Assert.Equal(Path.Combine(gameDir, "BepInEx", "LogOutput.log"), info.LogPath);
    }

    [Fact]
    public void Detect_WithMelonLoaderFolder_ReturnsMelonLoaderAndLogPath()
    {
        var gameDir = Path.Combine(_tempDirectory, "Game");
        Directory.CreateDirectory(Path.Combine(gameDir, "MelonLoader"));
        var exePath = Path.Combine(gameDir, "Game.exe");
        File.WriteAllBytes(exePath, Array.Empty<byte>());

        var info = ModLoaderDetector.Detect(exePath);

        Assert.Equal(ModLoaderKind.MelonLoader, info.Kind);
        Assert.Equal(Path.Combine(gameDir, "MelonLoader", "Latest.log"), info.LogPath);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDirectory))
            {
                Directory.Delete(_tempDirectory, recursive: true);
            }
        }
        catch
        {
            // Best-effort test cleanup.
        }
    }
}