using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using GameHelper.Infrastructure.Artwork;

namespace GameHelper.Tests;

public sealed class SteamArtworkTests
{
    private static readonly byte[] Poster = [255, 216, 12, 34, 255, 217];

    [Theory]
    [InlineData("620", 620u)]
    [InlineData("steam://rungameid/620/", 620u)]
    [InlineData("https://store.steampowered.com/app/620/Portal_2/", 620u)]
    [InlineData("0", null)]
    [InlineData("-1", null)]
    [InlineData("https://example.com/app/620/", null)]
    [InlineData("steam://rungameid/620.exe", null)]
    public void Identity_RequiresExplicitValidSteamId(string input, uint? expected) => Assert.Equal(expected, SteamArtworkIdentity.Parse(input));

    [Fact]
    public void Identity_ResolvesNestedInstallManifestAndShortcutWithoutRunningAnything()
    {
        using var root = new TemporaryDirectory();
        var install = Path.Combine(root.Path, "steamapps", "common", "Example");
        Directory.CreateDirectory(Path.Combine(install, "Binaries", "Win64"));
        File.WriteAllText(Path.Combine(root.Path, "steamapps", "appmanifest_620.acf"), "\"AppState\" { \"appid\" \"620\" \"installdir\" \"Example\" }");
        Assert.Equal(620u, SteamArtworkIdentity.Resolve(Path.Combine(install, "Binaries", "Win64", "game.exe"), string.Empty));
        var shortcut = Path.Combine(root.Path, "game.url");
        File.WriteAllText(shortcut, "[InternetShortcut]\nURL=steam://rungameid/621");
        Assert.Equal(621u, SteamArtworkIdentity.Resolve("game.exe", shortcut));
        File.WriteAllText(Path.Combine(install, "steam_appid.txt"), "622\n");
        Assert.Equal(620u, SteamArtworkIdentity.Resolve(Path.Combine(install, "game.exe"), string.Empty));
        File.WriteAllText(Path.Combine(root.Path, "steam_appid.txt"), "623\n");
        Assert.Equal(623u, SteamArtworkIdentity.Resolve(Path.Combine(root.Path, "standalone.exe"), string.Empty));
    }

    [Fact]
    public async Task Download_UsesHashedLibraryAssetDeduplicatesAndSurvivesOfflineRestart()
    {
        using var root = new TemporaryDirectory();
        using var handler = new Handler(request => request.RequestUri!.Host == "api.steampowered.com"
            ? Metadata() : Image(Poster));
        using var http = new HttpClient(handler);
        var client = new SteamArtworkClient(http, root.Path, bytes => bytes.SequenceEqual(Poster));
        var paths = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => client.GetPosterAsync(620, CancellationToken.None)));
        Assert.All(paths, path => Assert.Equal(paths[0], path));
        Assert.Equal(2, handler.Count);
        Assert.Contains(handler.Uris, uri => uri.AbsolutePath.EndsWith("/hash/library_600x900_2x.jpg", StringComparison.Ordinal));
        Assert.Equal(Poster, await File.ReadAllBytesAsync(paths[0]!));
        var restarted = new SteamArtworkClient(http, root.Path, bytes => bytes.SequenceEqual(Poster));
        Assert.Equal(paths[0], await restarted.GetPosterAsync(620, CancellationToken.None));
        Assert.Equal(2, handler.Count);
        Assert.Empty(Directory.GetFiles(root.Path, "*.tmp"));
    }

    [Fact]
    public async Task Download_RejectsHtmlAndBacksOffWithoutPublishingBadCache()
    {
        using var root = new TemporaryDirectory();
        using var handler = new Handler(request => request.RequestUri!.Host == "api.steampowered.com"
            ? Metadata() : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("<html>blocked</html>") });
        using var http = new HttpClient(handler);
        var clock = new Clock();
        var client = new SteamArtworkClient(http, root.Path, _ => true, clock);
        Assert.Null(await client.GetPosterAsync(620, CancellationToken.None));
        var requests = handler.Count;
        Assert.True(requests > 1);
        Assert.False(File.Exists(Path.Combine(root.Path, "620.jpg")));
        var reopened = new SteamArtworkClient(http, root.Path, _ => true, clock);
        Assert.Null(await reopened.GetPosterAsync(620, CancellationToken.None));
        Assert.Equal(requests, handler.Count);
        clock.Now += TimeSpan.FromMinutes(6);
        var later = new SteamArtworkClient(http, root.Path, _ => true, clock);
        Assert.Null(await later.GetPosterAsync(620, CancellationToken.None));
        Assert.True(handler.Count > requests);
    }

    [Fact]
    public async Task Download_RepairsCorruptCacheAndFailedRefreshRetainsGoodPoster()
    {
        using var root = new TemporaryDirectory();
        var path = Path.Combine(root.Path, "620.jpg");
        await File.WriteAllTextAsync(path, "broken");
        var missing = false;
        using var handler = new Handler(request => request.RequestUri!.Host == "api.steampowered.com" ? Metadata()
            : missing ? new HttpResponseMessage(HttpStatusCode.NotFound) : Image(Poster));
        using var http = new HttpClient(handler);
        var client = new SteamArtworkClient(http, root.Path, bytes => bytes.SequenceEqual(Poster));
        Assert.Equal(path, await client.GetPosterAsync(620, CancellationToken.None));
        missing = true;
        Assert.Equal(path, await client.GetPosterAsync(620, CancellationToken.None, refresh: true));
        Assert.Equal(Poster, await File.ReadAllBytesAsync(path));
    }

    [Fact]
    public async Task Download_RejectsOversizedImageAndHonorsCancellation()
    {
        using var root = new TemporaryDirectory();
        using var handler = new Handler(request =>
        {
            if (request.RequestUri!.Host == "api.steampowered.com") return Metadata();
            var response = Image(Poster);
            response.Content.Headers.ContentLength = 6 * 1024 * 1024;
            return response;
        });
        using var http = new HttpClient(handler);
        var client = new SteamArtworkClient(http, root.Path, _ => true);
        Assert.Null(await client.GetPosterAsync(620, CancellationToken.None));
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.GetPosterAsync(621, cancelled.Token));
        Assert.Empty(Directory.GetFiles(root.Path, "*.jpg"));
    }

    [Theory]
    [InlineData("https://evil.example/${FILENAME}", "cover.jpg")]
    [InlineData("steam/apps/621/${FILENAME}", "cover.jpg")]
    [InlineData("steam/apps/620/${FILENAME}", "../../cover.jpg")]
    [InlineData("steam/apps/620/${FILENAME}", "https://evil.example/cover.jpg")]
    public void Metadata_RejectsExternalOrMismatchedAssetPaths(string template, string filename)
    {
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(new Dictionary<string, string>
        { ["asset_url_format"] = template, ["library_capsule"] = filename }));
        Assert.Null(SteamArtworkClient.TryAssetUrl(620, json.RootElement, "library_capsule"));
    }

    [Fact]
    public async Task Search_EncodesNameAndReturnsDistinctAppsWithoutMakingAnAssociation()
    {
        using var root = new TemporaryDirectory();
        using var handler = new Handler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        { Content = new StringContent("""{"items":[{"id":620,"name":"Portal 2","type":"app"},{"id":620,"name":"Portal 2","type":"app"},{"id":7,"name":"Bundle","type":"sub"}]}""", Encoding.UTF8, "application/json") });
        using var http = new HttpClient(handler);
        var client = new SteamArtworkClient(http, root.Path, _ => false);
        var game = Assert.Single(await client.SearchAsync("Portal & 2", CancellationToken.None));
        Assert.Equal(620u, game.AppId);
        Assert.Contains("term=Portal%20%26%202", handler.Uris.Single().AbsoluteUri);
        Assert.Empty(Directory.GetFiles(root.Path));
    }

    private static HttpResponseMessage Metadata() => new(HttpStatusCode.OK)
    {
        Content = new StringContent("""{"response":{"store_items":[{"appid":620,"assets":{"asset_url_format":"steam/apps/620/${FILENAME}?t=1","library_capsule_2x":"hash/library_600x900_2x.jpg"}}]}}""", Encoding.UTF8, "application/json")
    };
    private static HttpResponseMessage Image(byte[] bytes)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };
        response.Content.Headers.ContentType = new("image/jpeg");
        return response;
    }
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        private int _count;
        public int Count => _count;
        public System.Collections.Concurrent.ConcurrentQueue<Uri> Uris { get; } = new();
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        { Interlocked.Increment(ref _count); Uris.Enqueue(request.RequestUri!); return Task.FromResult(respond(request)); }
    }
    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = DateTimeOffset.UnixEpoch.AddDays(20000);
        public override DateTimeOffset GetUtcNow() => Now;
    }
    private sealed class TemporaryDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "gh-steam-artwork", Guid.NewGuid().ToString("N"));
        public TemporaryDirectory() => Directory.CreateDirectory(Path);
        public void Dispose() { if (Directory.Exists(Path)) Directory.Delete(Path, true); }
    }
}
