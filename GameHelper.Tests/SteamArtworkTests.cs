using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using GameHelper.Infrastructure.Artwork;

namespace GameHelper.Tests;

public sealed class SteamArtworkTests
{
    private static readonly byte[] Poster = [255, 216, 12, 34, 255, 217];

    [Fact]
    public async Task LandscapeCaches_AreSeparateAndExplicitRefreshRecoversFromFailure()
    {
        using var root = new TemporaryDirectory();
        var offline = true;
        using var handler = new Handler(request => offline ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
            : request.RequestUri!.Host == "api.steampowered.com" ? Metadata() : Image(Poster));
        using var http = new HttpClient(handler);
        var client = new SteamArtworkClient(http, root.Path, bytes => bytes.SequenceEqual(Poster));
        Assert.Null(await client.GetCapsuleAsync(620, CancellationToken.None));
        Assert.Null(await client.GetHeaderAsync(620, CancellationToken.None));
        offline = false;
        Assert.Null(await client.GetHeaderAsync(620, CancellationToken.None));
        var capsule = await client.GetCapsuleAsync(620, CancellationToken.None, refresh: true);
        var header = await client.GetHeaderAsync(620, CancellationToken.None, refresh: true);
        Assert.EndsWith("620.capsule.jpg", capsule);
        Assert.EndsWith("620.header.jpg", header);
        Assert.NotEqual(capsule, header);
        Assert.Contains(handler.Uris, uri => uri.AbsolutePath.EndsWith("capsule_616x353.jpg", StringComparison.Ordinal));
        Assert.Contains(handler.Uris, uri => uri.AbsolutePath.EndsWith("header.jpg", StringComparison.Ordinal));
        Assert.Equal(header, client.GetCachedHeader(620));
        Assert.Empty(Directory.GetFiles(root.Path, "*.tmp"));
    }

    [Fact]
    public void NameMatching_RequiresUniqueExactTitleAndKeepsEditionNumbersSignificant()
    {
        Assert.Equal(620u, SteamArtworkNameMatcher.ExactMatch("Portal 2™", [new(620, "PORTAL: 2"), new(621, "Portal 2 Deluxe Edition")])?.AppId);
        Assert.Null(SteamArtworkNameMatcher.ExactMatch("Portal", [new(620, "Portal 2")]));
        Assert.Null(SteamArtworkNameMatcher.ExactMatch("Game", [new(1, "Game"), new(2, "Game")]));
        Assert.Null(SteamArtworkNameMatcher.ExactMatch("游戏", [new(1, "游戏 DLC")]));
        Assert.Null(SteamArtworkNameMatcher.ExactMatch("", [new(1, "Anything")]));
        Assert.Equal(1u, SteamArtworkNameMatcher.ExactMatch("游戏２", [new(1, "游戏2")])?.AppId);
    }

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
        Assert.All(handler.Uris.Where(uri => uri.Host == "store.steampowered.com"), uri => Assert.Contains("term=Portal%20%26%202", uri.AbsoluteUri));
        Assert.Empty(Directory.GetFiles(root.Path));
    }

    [Fact]
    public void BestMatch_UsesEnglishAliasRejectsSupplementSequelsEditionsAndAmbiguity()
    {
        var game = new SteamArtworkGame(1280930, "星界战士", "Astral Ascent", true);
        var dlc = new SteamArtworkGame(2633220, "星界战士画集", "Astral Ascent Artbook", false);
        Assert.Equal(game, SteamArtworkNameMatcher.BestMatch("Astral Ascent", [dlc, game]));
        Assert.Equal(game, SteamArtworkNameMatcher.BestMatch("Astral Ascnet.exe - 快捷方式", [dlc, game]));
        Assert.Null(SteamArtworkNameMatcher.BestMatch("Astral Ascent", [dlc]));
        Assert.Null(SteamArtworkNameMatcher.BestMatch("Slay the Spire 2", [new(1, "Slay the Spire", IsGame: true)]));
        Assert.Null(SteamArtworkNameMatcher.BestMatch("Resident Evil VII", [new(1, "Resident Evil VIII", IsGame: true)]));
        Assert.Null(SteamArtworkNameMatcher.BestMatch("Astral Ascent", [game, game with { AppId = 2 }]));
        Assert.Null(SteamArtworkNameMatcher.BestMatch("Astral Ascent", [new(1, "Astral Ascent Deluxe", IsGame: true)]));
        Assert.Equal(1u, SteamArtworkNameMatcher.BestMatch("Hades 2", [new(1, "Hades II", IsGame: true)])?.AppId);
    }

    [Fact]
    public void BestMatch_StripsLauncherVersionTailsAndTrailingReleaseAliases()
    {
        var orc = new SteamArtworkGame(1129540, "Orc Massage", null, true);
        var peeping = new SteamArtworkGame(2273420, "Peeping Dorm Manager", null, true);
        var remix = new SteamArtworkGame(2173930, "NTRaholic Remix", null, true);
        var sequel = new SteamArtworkGame(3412290, "NTRaholic SS", null, true);
        // Launcher-style names with version tails bind their exact Steam base game.
        Assert.Equal(orc, SteamArtworkNameMatcher.BestMatch("Orc.Massage.v07.08.2023", [orc]));
        Assert.Equal(peeping, SteamArtworkNameMatcher.ExactMatch("Peeping.Dorm.Manager.v1.0.9", [peeping]));
        // A trailing release alias on one side only stays a confident match on both directions.
        Assert.Equal(remix, SteamArtworkNameMatcher.BestMatch("NTRaholic", [remix, sequel]));
        Assert.Null(SteamArtworkNameMatcher.BestMatch("NTRaholic", [sequel]));
        Assert.Equal(2897760u, SteamArtworkNameMatcher.BestMatch("NTRaholic Remix", [new(2897760, "NTRaholic", IsGame: true)])?.AppId);
        Assert.Equal(1u, SteamArtworkNameMatcher.BestMatch("Wandering Sword HD", [new(1, "Wandering Sword", IsGame: true)])?.AppId);
        // Stripping stops at bare sequel numbers and unknown tails; edition words still require both sides.
        Assert.Null(SteamArtworkNameMatcher.BestMatch("Mass Effect 2.v1.0.3", [new(1, "Mass Effect", IsGame: true)]));
        Assert.Null(SteamArtworkNameMatcher.BestMatch("Slay the Spire 2", [new(1, "Slay the Spire", IsGame: true)]));
        Assert.Null(SteamArtworkNameMatcher.BestMatch("Astral Ascent", [new(1, "Astral Ascent Deluxe", IsGame: true)]));
    }

    [Fact]
    public async Task Search_MergesLocalizedAliasesAndVerifiesBaseGameTypeBeforeAutomaticChoice()
    {
        using var root = new TemporaryDirectory();
        using var handler = new Handler(request => new(HttpStatusCode.OK)
        { Content = new StringContent(request.RequestUri!.Host == "api.steampowered.com"
            ? """{"response":{"store_items":[{"appid":1280930,"name":"Astral Ascent","type":0},{"appid":2633220,"name":"Astral Ascent Artbook","type":4}]}}"""
            : request.RequestUri.Query.Contains("l=english", StringComparison.Ordinal)
                ? """{"items":[{"id":1280930,"name":"Astral Ascent","type":"app"},{"id":2633220,"name":"Astral Ascent Artbook","type":"app"}]}"""
                : """{"items":[{"id":1280930,"name":"星界战士","type":"app"},{"id":2633220,"name":"星界战士画集","type":"app"}]}""") });
        using var http = new HttpClient(handler);
        var results = await new SteamArtworkClient(http, root.Path, _ => false).SearchAsync("Astral Ascent", CancellationToken.None);
        Assert.Equal(2, results.Count); Assert.Equal(1280930u, results[0].AppId);
        Assert.Equal("星界战士", results[0].Name); Assert.Equal("Astral Ascent", results[0].EnglishName);
        Assert.True(results[0].IsGame); Assert.False(results[1].IsGame);
        Assert.Equal(1280930u, SteamArtworkNameMatcher.BestMatch("Astral Ascent", results)?.AppId);
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
