using GameHelper.Core.Models;
using GameHelper.Infrastructure.Artwork;
using GameHelper.Infrastructure.Storage.Sqlite;
using System.Net;

namespace GameHelper.Tests;

public sealed class SteamControllerSupportTests
{
    [Theory]
    [InlineData("\"controller_support\":\"full\",", "full")]
    [InlineData("\"controller_support\":\"partial\",", "partial")]
    [InlineData("", "unlisted")]
    public void ReadsDeclarations(string field, string expected)
    {
        var result = SteamControllerSupportClient.Parse("{\"620\":{\"success\":true,\"data\":{" + field + "\"categories\":[{\"id\":55},{\"id\":58}]}}}", 620);
        Assert.Equal(expected, result.Status); Assert.True(result.DualShock); Assert.True(result.DualSense);
        Assert.Equal(expected == "unlisted", result.Unsupported);
        Assert.False((result with { Manual = true }).Unsupported);
        Assert.True((result with { Manual = false }).Unsupported);
    }
    [Theory]
    [InlineData(28, "full")]
    [InlineData(18, "partial")]
    public void CategoryFallback(int category, string expected) => Assert.Equal(expected,
        SteamControllerSupportClient.Parse($"{{\"1\":{{\"success\":true,\"data\":{{\"categories\":[{{\"id\":{category}}}]}}}}}}", 1).Status);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedRequestsAreUnknownAndBackOff(bool networkFailure)
    {
        var dir = Path.Combine(Path.GetTempPath(), "gh-controller-" + Guid.NewGuid()); Directory.CreateDirectory(dir);
        try
        {
            var factory = new SqliteConnectionFactory(dir); await new SqliteDatabaseInitializer(factory).InitializeAsync();
            var clock = new FixedClock(); var store = new SqliteControllerSupportStore(factory, clock);
            var handler = new Handler(networkFailure); using var http = new HttpClient(handler);
            var client = new SteamControllerSupportClient(http, store, clock);
            Assert.Equal("unknown", (await client.GetAsync(1, true, default)).Status);
            Assert.False((await client.GetAsync(1, true, default)).Unsupported);
            Assert.Equal(1, handler.Calls);
            store.Save(2, new("full"), clock.GetUtcNow().AddDays(-1), 0);
            Assert.Equal("full", (await client.GetAsync(2, true, default)).Status);
            Assert.Equal("full", (await client.GetAsync(2, false, default)).Status);
        }
        finally { Directory.Delete(dir, true); }
    }
    [Fact]
    public async Task CacheAndManualOverridesPersistWithoutAffectingOtherScopes()
    {
        var dir = Path.Combine(Path.GetTempPath(), "gh-controller-" + Guid.NewGuid()); Directory.CreateDirectory(dir);
        try
        {
            var factory = new SqliteConnectionFactory(dir); await new SqliteDatabaseInitializer(factory).InitializeAsync();
            var config = Path.Combine(dir, "a.yml"); var other = Path.Combine(dir, "b.yml");
            using (var c = factory.OpenConnection())
            using (var cmd = c.CreateCommand())
            {
                cmd.CommandText = "INSERT INTO config_scopes VALUES(1,$a),(2,$b);";
                cmd.Parameters.AddWithValue("$a", config); cmd.Parameters.AddWithValue("$b", other); cmd.ExecuteNonQuery();
            }
            var clock = new FixedClock(); var store = new SqliteControllerSupportStore(factory, clock);
            store.SaveManual(config, "Game", false);
            Assert.False(store.Manual(config, "GAME")); Assert.Null(store.Manual(other, "Game"));
            store.SaveManual(config, "Game", null); Assert.Null(store.Manual(config, "Game"));
            store.Save(620, new("partial", true, false), clock.GetUtcNow().AddDays(30), 0);
            using var http = new HttpClient(new Handler(true)); var client = new SteamControllerSupportClient(http, store, clock);
            Assert.Equal(new ControllerSupport("partial", true, false), await client.GetAsync(620, true, default));
        }
        finally { Directory.Delete(dir, true); }
    }
    private sealed class FixedClock : TimeProvider
    { public override DateTimeOffset GetUtcNow() => DateTimeOffset.UnixEpoch.AddDays(20000); }
    private sealed class Handler(bool fail) : HttpMessageHandler
    {
        public int Calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Calls++;
            if (fail) throw new HttpRequestException("offline");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"1\":{\"success\":false},\"2\":{\"success\":false}}") });
        }
    }
}
