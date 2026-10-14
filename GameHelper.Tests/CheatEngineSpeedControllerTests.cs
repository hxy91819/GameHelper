using System.Diagnostics;
using System.Xml.Linq;
using GameHelper.Core.Abstractions;
using GameHelper.Core.Models;
using GameHelper.Infrastructure.Controllers;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace GameHelper.Tests;

public sealed class CheatEngineSpeedControllerTests : IDisposable
{
    private readonly string _tempDirectory = Path.Combine(
        Path.GetTempPath(),
        "GameHelperTests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public void ApplySpeed_GeneratesTableForPidAndLaunchesConfiguredCheatEngine()
    {
        Directory.CreateDirectory(_tempDirectory);
        var cheatEnginePath = Path.Combine(_tempDirectory, "cheatengine-x86_64.exe");
        File.WriteAllBytes(cheatEnginePath, Array.Empty<byte>());
        var tableDirectory = Path.Combine(_tempDirectory, "tables");
        ProcessStartInfo? launched = null;
        var configuration = new Mock<IGameConfiguration>();
        configuration.Setup(provider => provider.Read()).Returns(new AppConfig
        {
            CheatEngine = new CheatEngineSettings { Executable = cheatEnginePath }
        });
        var controller = new CheatEngineSpeedController(
            configuration.Object,
            NullLogger<CheatEngineSpeedController>.Instance,
            tableDirectory,
            startInfo => launched = startInfo);

        controller.ApplySpeed(4321, 2.5);

        Assert.NotNull(launched);
        Assert.Equal(cheatEnginePath, launched.FileName);
        var tablePath = launched.Arguments.Trim('"');
        Assert.Equal(Path.Combine(tableDirectory, "speed-4321.ct"), tablePath);
        var document = XDocument.Load(tablePath);
        var lua = document.Root?.Element("LuaScript")?.Value;
        Assert.Contains("local targetPid = 4321", lua);
        Assert.Contains("local targetSpeed = 2.5", lua);
        Assert.Contains("openProcess(targetPid)", lua);
        Assert.Contains("speedhack_setSpeed(targetSpeed)", lua);
    }

    [Fact]
    public void ApplySpeed_WithMissingExecutable_DoesNotLaunch()
    {
        var configuration = new Mock<IGameConfiguration>();
        configuration.Setup(provider => provider.Read()).Returns(new AppConfig());
        var launches = 0;
        var controller = new CheatEngineSpeedController(
            configuration.Object,
            NullLogger<CheatEngineSpeedController>.Instance,
            _tempDirectory,
            _ => launches++);

        controller.ApplySpeed(4321, 2);

        Assert.Equal(0, launches);
        Assert.False(Directory.Exists(_tempDirectory));
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
