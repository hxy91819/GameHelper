using GameHelper.ConsoleHost.Services;
using GameHelper.Core.Abstractions;
using GameHelper.Core.Services;
using GameHelper.Infrastructure.Providers;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace GameHelper.Tests;

public sealed class FileDropHandlerTests : IDisposable
{
    private readonly string _tempDirectory;
    private readonly string _configPath;
    private readonly YamlConfigProvider _configuration;
    private readonly Mock<ISteamGameResolver> _steamResolver = new(MockBehavior.Strict);
    private readonly Mock<IGameAutomationService> _automation = new(MockBehavior.Strict);
    private readonly FileDropIntake _intake;

    public FileDropHandlerTests()
    {
        _tempDirectory = Path.Combine(Path.GetTempPath(), "GameHelperFileDropBehaviorTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDirectory);
        _configPath = Path.Combine(_tempDirectory, "config.yml");
        _configuration = new YamlConfigProvider(_configPath);
        _intake = new FileDropIntake(
            new GameCatalogService(_configuration),
            _steamResolver.Object,
            _automation.Object,
            _configuration,
            NullLogger<FileDropIntake>.Instance);
    }

    [Fact]
    public void LooksLikeFilePaths_AcceptsExistingExeAndUrlCaseInsensitively()
    {
        var executablePath = CreateFile("Game.EXE");
        var shortcutPath = CreateFile("Steam Game.URL", "URL=steam://rungameid/42");

        var result = FileDropHandler.LooksLikeFilePaths([executablePath, shortcutPath]);

        Assert.True(result);
    }

    [Fact]
    public void LooksLikeFilePaths_RejectsUnsupportedOrMissingFiles()
    {
        var textPath = CreateFile("notes.txt");
        var missingExecutablePath = Path.Combine(_tempDirectory, "missing.exe");

        Assert.False(FileDropHandler.LooksLikeFilePaths([textPath]));
        Assert.False(FileDropHandler.LooksLikeFilePaths([missingExecutablePath]));
        Assert.False(FileDropHandler.LooksLikeFilePaths([]));
    }

    [Fact]
    public async Task HandleAsync_Executable_StoresSingleIdentityAndFileNameDisplay()
    {
        var executablePath = CreateFile("My Special Game.EXE");
        _automation.Setup(service => service.ReloadConfig());

        var result = await _intake.HandleAsync(
            new DropAddRequest { Paths = [executablePath] },
            CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal(1, result.Added);
        var game = Assert.Single(_configuration.Read().Games);
        Assert.Equal("myspecialgame", game.DataKey);
        Assert.Equal(executablePath, game.Executable);
        Assert.Equal("My Special Game.EXE", game.ExecutableName);
        Assert.Equal("My Special Game", game.DisplayName);
        Assert.True(game.IsEnabled);
        Assert.False(game.HdrEnabled);
        _automation.Verify(service => service.ReloadConfig(), Times.Once);
        _steamResolver.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task HandleAsync_SteamUrl_ResolvesExecutableAndUsesShortcutDisplayName()
    {
        var shortcutPath = CreateFile("Steam Adventure.url", "URL=steam://rungameid/4242");
        var executablePath = CreateFile("SteamAdventure.exe");
        _steamResolver
            .Setup(resolver => resolver.TryParseInternetShortcutUrl(shortcutPath))
            .Returns("steam://rungameid/4242");
        _steamResolver
            .Setup(resolver => resolver.TryParseRunGameId("steam://rungameid/4242"))
            .Returns("4242");
        _steamResolver
            .Setup(resolver => resolver.TryResolveExeFromAppId("4242"))
            .Returns(executablePath);
        _automation.Setup(service => service.ReloadConfig());

        var result = await _intake.HandleAsync(
            new DropAddRequest { Paths = [shortcutPath] },
            CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal(1, result.Added);
        var game = Assert.Single(_configuration.Read().Games);
        Assert.Equal("steamadventure", game.DataKey);
        Assert.Equal(executablePath, game.Executable);
        Assert.Equal("SteamAdventure.exe", game.ExecutableName);
        Assert.Equal("Steam Adventure", game.DisplayName);
        _steamResolver.VerifyAll();
        _automation.Verify(service => service.ReloadConfig(), Times.Once);
    }

    private string CreateFile(string fileName, string content = "")
    {
        var path = Path.Combine(_tempDirectory, fileName);
        File.WriteAllText(path, content);
        return path;
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDirectory))
        {
            Directory.Delete(_tempDirectory, recursive: true);
        }
    }
}
