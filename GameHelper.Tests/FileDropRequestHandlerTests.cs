using GameHelper.ConsoleHost.Services;
using GameHelper.Core.Abstractions;
using GameHelper.Core.Models;
using GameHelper.Core.Services;
using GameHelper.Infrastructure.Providers;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace GameHelper.Tests;

public sealed class FileDropRequestHandlerTests : IDisposable
{
    private readonly string _tempDirectory;
    private readonly string _configPath;
    private readonly YamlConfigProvider _yamlConfiguration;
    private readonly CountingGameConfiguration _configuration;
    private readonly Mock<ISteamGameResolver> _steamResolver = new(MockBehavior.Strict);
    private readonly Mock<IGameAutomationService> _automation = new(MockBehavior.Strict);
    private readonly FileDropIntake _intake;

    public FileDropRequestHandlerTests()
    {
        _tempDirectory = Path.Combine(Path.GetTempPath(), "GameHelperFileDropTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDirectory);
        _configPath = Path.Combine(_tempDirectory, "config.yml");
        _yamlConfiguration = new YamlConfigProvider(_configPath);
        _configuration = new CountingGameConfiguration(_yamlConfiguration);
        _intake = new FileDropIntake(
            new GameCatalogService(_configuration),
            _steamResolver.Object,
            _automation.Object,
            _yamlConfiguration,
            NullLogger<FileDropIntake>.Instance);
    }

    [Fact]
    public async Task HandleAsync_MultipleExecutables_CommitsBatchOnceAndReloadsOnce()
    {
        var firstPath = CreateFile("FirstAdventure.exe");
        var secondPath = CreateFile("SecondAdventure.EXE");
        _automation.Setup(service => service.ReloadConfig());

        var result = await _intake.HandleAsync(
            new DropAddRequest { Paths = [firstPath, secondPath] },
            CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal(2, result.Added);
        Assert.Equal(0, result.Updated);
        Assert.Equal(0, result.Skipped);
        Assert.Equal(_configPath, result.ConfigPath);
        Assert.Equal(1, _configuration.ChangeCalls);

        var games = _yamlConfiguration.Read().Games;
        Assert.Equal(2, games.Count);
        Assert.Contains(games, game => game.Executable == firstPath);
        Assert.Contains(games, game => game.Executable == secondPath);

        _automation.Verify(service => service.ReloadConfig(), Times.Once);
        _steamResolver.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task HandleAsync_InvalidPayload_DoesNotCommitOrReload()
    {
        var invalidPath = CreateFile("not-a-game.txt");

        var result = await _intake.HandleAsync(
            new DropAddRequest { Paths = [invalidPath] },
            CancellationToken.None);

        Assert.False(result.Success);
        Assert.NotNull(result.Error);
        Assert.Contains("Only existing .exe/.lnk/.url", result.Error);
        Assert.Equal(0, _configuration.ChangeCalls);
        Assert.False(File.Exists(_configPath));
        _automation.Verify(service => service.ReloadConfig(), Times.Never);
        _steamResolver.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task HandleAsync_ExistingGame_PreservesHdrPreference()
    {
        var executablePath = CreateFile("ExistingGame.exe");
        _yamlConfiguration.Change(config => config.Games =
        [
            new GameConfig
            {
                DataKey = "existing-game",
                Executable = executablePath,
                DisplayName = "Existing Game",
                IsEnabled = true,
                HdrEnabled = true
            }
        ]);
        _automation.Setup(service => service.ReloadConfig());

        var result = await _intake.HandleAsync(
            new DropAddRequest { Paths = [executablePath] },
            CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal(0, result.Added);
        Assert.Equal(1, result.Updated);
        Assert.True(Assert.Single(_yamlConfiguration.Read().Games).HdrEnabled);
        _automation.Verify(service => service.ReloadConfig(), Times.Once);
        _steamResolver.VerifyNoOtherCalls();
    }

    private string CreateFile(string fileName)
    {
        var path = Path.Combine(_tempDirectory, fileName);
        File.WriteAllText(path, string.Empty);
        return path;
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDirectory))
        {
            Directory.Delete(_tempDirectory, recursive: true);
        }
    }

    private sealed class CountingGameConfiguration : IGameConfiguration
    {
        private readonly IGameConfiguration _inner;

        public CountingGameConfiguration(IGameConfiguration inner)
        {
            _inner = inner;
        }

        public int ChangeCalls { get; private set; }

        public AppConfig Read() => _inner.Read();

        public AppConfig Change(Action<AppConfig> change)
        {
            ChangeCalls++;
            return _inner.Change(change);
        }
    }
}
