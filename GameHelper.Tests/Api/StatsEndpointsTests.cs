using System.Net;
using System.Net.Http.Json;
using GameHelper.ConsoleHost.Api.Models;
using GameHelper.Core.Models;

namespace GameHelper.Tests.Api;

public sealed class StatsEndpointsTests : IDisposable
{
    private readonly HttpClient _client;
    private readonly InMemoryConfigProvider _provider;

    public StatsEndpointsTests()
    {
        (_client, _provider) = ApiTestHelper.CreateTestServer();
    }

    public void Dispose() => _client.Dispose();

    [Fact]
    public async Task GetStats_Returns200WithArray()
    {
        var response = await _client.GetAsync("/api/stats");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var stats = await response.Content.ReadFromJsonAsync<GameStatsDto[]>();
        Assert.NotNull(stats);
        // Response is a valid array (may contain data if playtime files exist on test machine)
        foreach (var s in stats)
        {
            Assert.False(string.IsNullOrWhiteSpace(s.GameName));
            Assert.True(s.TotalMinutes >= 0);
            Assert.True(s.SessionCount >= 0);
        }
    }

    [Fact]
    public async Task GetStatsByGame_NonexistentUuid_Returns404()
    {
        // Use a UUID that will never match a real game name
        var response = await _client.GetAsync($"/api/stats/{Guid.NewGuid()}");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
}
