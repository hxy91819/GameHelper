using System.Net;
using System.Net.Http;
using GameHelper.Infrastructure.Artwork;
using GameHelper.Desktop;

namespace GameHelper.Tests;

public sealed class SteamGenreTests
{
    [Fact]
    public async Task Genres_CachePersistsAcrossClientsAndOfflineDoesNotFetch()
    {
        using var files = new Files();
        var clock = new Clock();
        using var handler = new Handler(); using var http = new HttpClient(handler);
        var client = new SteamGenreClient(http, files.Root, clock);
        Assert.Equal(new[] { "动作", "角色扮演" }, await client.GetAsync(123, true));
        Assert.Equal(1, handler.Calls);
        var reopened = new SteamGenreClient(http, files.Root, clock);
        Assert.Equal(new[] { "动作", "角色扮演" }, await reopened.GetAsync(123, false));
        Assert.Equal(new[] { "动作", "角色扮演" }, await reopened.GetAsync(123, true));
        Assert.Equal(1, handler.Calls);
        Assert.Empty(await reopened.GetAsync(999, false));
        Assert.False(File.Exists(Path.Combine(files.Root, "999.json")));
    }

    [Fact]
    public async Task FailedRefresh_KeepsStaleGenresAndBacksOffBeforeRetry()
    {
        using var files = new Files(); var clock = new Clock();
        using var handler = new Handler(); using var http = new HttpClient(handler);
        var client = new SteamGenreClient(http, files.Root, clock);
        await client.GetAsync(123, true);
        clock.Now = clock.Now.AddDays(31); handler.Fail = true;
        Assert.Equal(new[] { "动作", "角色扮演" }, await client.GetAsync(123, true));
        Assert.Equal(2, handler.Calls);
        clock.Now = clock.Now.AddHours(2);
        await client.GetAsync(123, true); Assert.Equal(2, handler.Calls);
        clock.Now = clock.Now.AddHours(6); handler.Fail = false;
        await client.GetAsync(123, true); Assert.Equal(3, handler.Calls);
        Assert.Empty(Directory.GetFiles(files.Root, "*.tmp"));
    }

    [Fact]
    public void ManualOverride_EmptyAndMultipleGenresSurviveReopenAndReset()
    {
        using var files = new Files(); var config = Path.Combine(files.Root, "config.yml");
        File.WriteAllText(config, "original");
        var store = new LibraryGenreStore(config);
        store.Save("a", [" 动作 ", "角色扮演", "动作"]);
        var reopened = new LibraryGenreStore(config);
        Assert.True(reopened.TryRead("A", out var genres));
        Assert.Equal(new[] { "动作", "角色扮演" }, genres);
        reopened.Save("a", []);
        Assert.True(new LibraryGenreStore(config).TryRead("a", out genres)); Assert.Empty(genres);
        reopened.Save("a", null);
        Assert.False(new LibraryGenreStore(config).TryRead("a", out _));
        Assert.Equal("original", File.ReadAllText(config));
    }

    private sealed class Handler : HttpMessageHandler
    {
        public int Calls { get; private set; }
        public bool Fail { get; set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            Assert.Contains("filters=genres", request.RequestUri!.Query);
            return Task.FromResult(new HttpResponseMessage(Fail ? HttpStatusCode.TooManyRequests : HttpStatusCode.OK)
            { Content = new StringContent("""{"123":{"success":true,"data":{"genres":[{"id":"1","description":"动作"},{"id":"3","description":"角色扮演"}],"categories":[{"description":"单人"}]}}}""") });
        }
    }
    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = DateTimeOffset.UnixEpoch;
        public override DateTimeOffset GetUtcNow() => Now;
    }
    private sealed class Files : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "gh-genre-" + Guid.NewGuid().ToString("N"));
        public Files() => Directory.CreateDirectory(Root);
        public void Dispose() => Directory.Delete(Root, true);
    }
}
