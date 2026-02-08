using System.Net;
using System.Net.Http.Json;
using GameHelper.ConsoleHost.Api.Models;
using GameHelper.Core.Models;

namespace GameHelper.Tests.Api;

public sealed class SettingsEndpointsTests : IDisposable
{
    private readonly HttpClient _client;
    private readonly InMemoryConfigProvider _provider;

    public SettingsEndpointsTests()
    {
        (_client, _provider) = ApiTestHelper.CreateTestServer();
    }

    public void Dispose() => _client.Dispose();

    [Fact]
    public async Task GetSettings_ReturnsDefaults()
    {
        var response = await _client.GetAsync("/api/settings");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var dto = await response.Content.ReadFromJsonAsync<SettingsDto>();
        Assert.NotNull(dto);
        Assert.Equal("ETW", dto.ProcessMonitorType);
        Assert.False(dto.AutoStartInteractiveMonitor);
        Assert.False(dto.LaunchOnSystemStartup);
    }

    [Fact]
    public async Task PutSettings_UpdatesValues()
    {
        var request = new UpdateSettingsRequest
        {
            ProcessMonitorType = "WMI",
            AutoStartInteractiveMonitor = true,
            LaunchOnSystemStartup = true
        };

        var response = await _client.PutAsJsonAsync("/api/settings", request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var dto = await response.Content.ReadFromJsonAsync<SettingsDto>();
        Assert.NotNull(dto);
        Assert.Equal("WMI", dto.ProcessMonitorType);
        Assert.True(dto.AutoStartInteractiveMonitor);
        Assert.True(dto.LaunchOnSystemStartup);

        // Verify persisted
        var config = _provider.LoadAppConfig();
        Assert.Equal(ProcessMonitorType.WMI, config.ProcessMonitorType);
    }
}
