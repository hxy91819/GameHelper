using System.Net;
using System.Net.Http.Json;
using GameHelper.ConsoleHost.Api.Models;
using GameHelper.Core.Models;

namespace GameHelper.Tests.Api;

public sealed class GameEndpointsTests : IDisposable
{
    private readonly HttpClient _client;
    private readonly InMemoryConfigProvider _provider;

    public GameEndpointsTests()
    {
        (_client, _provider) = ApiTestHelper.CreateTestServer();
    }

    public void Dispose() => _client.Dispose();

    [Fact]
    public async Task GetGames_Empty_ReturnsEmptyArray()
    {
        var response = await _client.GetAsync("/api/games");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var games = await response.Content.ReadFromJsonAsync<GameDto[]>();
        Assert.NotNull(games);
        Assert.Empty(games);
    }

    [Fact]
    public async Task GetGames_WithData_ReturnsAll()
    {
        _provider.Seed(
            new GameConfig { DataKey = "game.exe", ExecutableName = "game.exe", DisplayName = "My Game", IsEnabled = true, HDREnabled = false });

        var games = await _client.GetFromJsonAsync<GameDto[]>("/api/games");
        Assert.NotNull(games);
        Assert.Single(games);
        Assert.Equal("game.exe", games[0].DataKey);
        Assert.Equal("My Game", games[0].DisplayName);
    }

    [Fact]
    public async Task PostGame_Valid_Returns201()
    {
        var request = new CreateGameRequest
        {
            ExecutableName = "test.exe",
            DisplayName = "Test Game",
            IsEnabled = true,
            HdrEnabled = true
        };

        var response = await _client.PostAsJsonAsync("/api/games", request);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var dto = await response.Content.ReadFromJsonAsync<GameDto>();
        Assert.NotNull(dto);
        Assert.Equal("test.exe", dto.DataKey);
        Assert.Equal("Test Game", dto.DisplayName);
        Assert.True(dto.HdrEnabled);

        // Verify persisted
        var configs = _provider.Load();
        Assert.True(configs.ContainsKey("test.exe"));
    }

    [Fact]
    public async Task PostGame_MissingName_Returns400()
    {
        var request = new CreateGameRequest { ExecutableName = "" };
        var response = await _client.PostAsJsonAsync("/api/games", request);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task PutGame_Existing_Returns200()
    {
        _provider.Seed(
            new GameConfig { DataKey = "game.exe", ExecutableName = "game.exe", DisplayName = "Old Name", IsEnabled = true });

        var request = new UpdateGameRequest
        {
            ExecutableName = "game.exe",
            DisplayName = "New Name",
            IsEnabled = false,
            HdrEnabled = true
        };

        var response = await _client.PutAsJsonAsync("/api/games/game.exe", request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var dto = await response.Content.ReadFromJsonAsync<GameDto>();
        Assert.NotNull(dto);
        Assert.Equal("New Name", dto.DisplayName);
        Assert.False(dto.IsEnabled);
        Assert.True(dto.HdrEnabled);
    }

    [Fact]
    public async Task PutGame_NotFound_Returns404()
    {
        var request = new UpdateGameRequest { ExecutableName = "nope.exe" };
        var response = await _client.PutAsJsonAsync("/api/games/nope.exe", request);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task DeleteGame_Existing_Returns204()
    {
        _provider.Seed(
            new GameConfig { DataKey = "game.exe", ExecutableName = "game.exe" });

        var response = await _client.DeleteAsync("/api/games/game.exe");
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.False(_provider.Load().ContainsKey("game.exe"));
    }

    [Fact]
    public async Task DeleteGame_NotFound_Returns404()
    {
        var response = await _client.DeleteAsync("/api/games/nope.exe");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
}
