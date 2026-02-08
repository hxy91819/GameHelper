using GameHelper.ConsoleHost.Api;
using GameHelper.Core.Abstractions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace GameHelper.Tests.Api;

internal static class ApiTestHelper
{
    public static (HttpClient Client, InMemoryConfigProvider Provider) CreateTestServer()
    {
        var provider = new InMemoryConfigProvider();

        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0"); // random port
        builder.Services.AddSingleton<IConfigProvider>(provider);
        builder.Services.AddSingleton<IAppConfigProvider>(provider);

        var app = builder.Build();
        app.MapGameEndpoints();
        app.MapSettingsEndpoints();
        app.MapStatsEndpoints();
        app.StartAsync().GetAwaiter().GetResult();

        var address = app.Urls.First();
        var client = new HttpClient { BaseAddress = new Uri(address) };

        return (client, provider);
    }
}
