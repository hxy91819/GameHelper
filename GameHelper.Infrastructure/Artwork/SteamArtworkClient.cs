using System.Net.Http;
using System.Text.Json;

namespace GameHelper.Infrastructure.Artwork;

public sealed record SteamArtworkGame(uint AppId, string Name, string? EnglishName = null, bool? IsGame = null);

/// <summary>Reads public Steam artwork metadata and atomically caches bounded image downloads.</summary>
public sealed class SteamArtworkClient(HttpClient http, string cacheDirectory,
    Func<byte[], bool> validateImage, TimeProvider? timeProvider = null)
{
    private const int ImageLimit = 5 * 1024 * 1024;
    private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;
    private readonly Dictionary<uint, Task<string?>> _requests = [];
    private readonly Dictionary<uint, Task<string?>> _capsules = [];
    private readonly Dictionary<uint, Task<string?>> _headers = [];
    private readonly object _requestGate = new();
    private readonly SemaphoreSlim _downloads = new(2);

    public string? GetCachedPoster(uint appId)
    {
        var path = Path.Combine(cacheDirectory, appId + ".jpg");
        return ValidCache(path) ? path : null;
    }

    public Task<string?> GetPosterAsync(uint appId, CancellationToken lifetime, bool refresh = false)
    {
        if (appId == 0) return Task.FromResult<string?>(null);
        lock (_requestGate)
        {
            if (refresh) _requests.Remove(appId);
            if (!_requests.TryGetValue(appId, out var task))
            {
                task = Task.Run(() => DownloadAsync(appId, lifetime, refresh), CancellationToken.None);
                _requests.Add(appId, task);
            }
            return task;
        }
    }

    /// <summary>Reads the landscape store capsule used by the library hero and detail banner.</summary>
    public string? GetCachedCapsule(uint appId)
    {
        var path = Path.Combine(cacheDirectory, appId + ".capsule.jpg");
        return ValidCache(path) ? path : null;
    }

    public Task<string?> GetCapsuleAsync(uint appId, CancellationToken lifetime, bool refresh = false)
    {
        if (appId == 0) return Task.FromResult<string?>(null);
        lock (_requestGate)
        {
            if (refresh) _capsules.Remove(appId);
            if (!_capsules.TryGetValue(appId, out var task))
            {
                task = Task.Run(() => DownloadAsync(appId, lifetime, refresh, wide: true), CancellationToken.None);
                _capsules.Add(appId, task);
            }
            return task;
        }
    }

    public string? GetCachedHeader(uint appId)
    {
        var path = Path.Combine(cacheDirectory, appId + ".header.jpg");
        return ValidCache(path) ? path : null;
    }
    public Task<string?> GetHeaderAsync(uint appId, CancellationToken lifetime, bool refresh = false)
    {
        if (appId == 0) return Task.FromResult<string?>(null);
        lock (_requestGate)
        {
            if (refresh) _headers.Remove(appId);
            if (!_headers.TryGetValue(appId, out var task))
            {
                task = Task.Run(() => DownloadAsync(appId, lifetime, refresh, wide: true, header: true), CancellationToken.None);
                _headers.Add(appId, task);
            }
            return task;
        }
    }

    public async Task<IReadOnlyList<SteamArtworkGame>> SearchAsync(string query, CancellationToken cancellationToken)
    {
        query = query.Trim();
        if (query.Length is < 1 or > 120) return [];
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(12));
        var found = new Dictionary<uint, SteamArtworkGame>();
        foreach (var language in new[] { "schinese", "english" })
        {
            try
            {
                var url = $"https://store.steampowered.com/api/storesearch/?l={language}&cc=CN&term=" + Uri.EscapeDataString(query);
                using var json = JsonDocument.Parse(await ReadAsync(url, 512 * 1024, timeout.Token));
                if (json.RootElement.ValueKind != JsonValueKind.Object || !json.RootElement.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array) continue;
                foreach (var item in items.EnumerateArray().Take(20))
                {
                    if (item.ValueKind != JsonValueKind.Object || !item.TryGetProperty("type", out var type) || type.ValueKind != JsonValueKind.String || type.GetString() != "app"
                        || !item.TryGetProperty("id", out var id) || id.ValueKind != JsonValueKind.Number || !id.TryGetUInt32(out var number) || number == 0
                        || !item.TryGetProperty("name", out var name) || name.ValueKind != JsonValueKind.String || name.GetString() is not { Length: > 0 and <= 300 } title) continue;
                    found[number] = found.TryGetValue(number, out var old)
                        ? old with { EnglishName = language == "english" ? title : old.EnglishName }
                        : new(number, title, language == "english" ? title : null);
                }
            }
            catch (Exception ex) when (ex is HttpRequestException or JsonException or InvalidDataException or OperationCanceledException) { cancellationToken.ThrowIfCancellationRequested(); }
        }
        if (found.Count > 0 && !timeout.IsCancellationRequested)
        {
            try
            {
                var input = JsonSerializer.Serialize(new { ids = found.Keys.Select(id => new { appid = id }), context = new { language = "english", country_code = "US" } });
                using var metadata = JsonDocument.Parse(await ReadAsync("https://api.steampowered.com/IStoreBrowseService/GetItems/v1/?input_json=" + Uri.EscapeDataString(input), 512 * 1024, timeout.Token));
                if (metadata.RootElement.ValueKind == JsonValueKind.Object && metadata.RootElement.TryGetProperty("response", out var response) && response.ValueKind == JsonValueKind.Object
                    && response.TryGetProperty("store_items", out var items) && items.ValueKind == JsonValueKind.Array)
                    foreach (var item in items.EnumerateArray())
                    {
                        if (item.ValueKind != JsonValueKind.Object || !item.TryGetProperty("appid", out var id) || id.ValueKind != JsonValueKind.Number || !id.TryGetUInt32(out var appId) || !found.TryGetValue(appId, out var old)) continue;
                        var english = item.TryGetProperty("name", out var name) && name.ValueKind == JsonValueKind.String && name.GetString() is { Length: > 0 and <= 300 } title ? title : old.EnglishName;
                        bool? game = item.TryGetProperty("type", out var type) && type.ValueKind == JsonValueKind.Number && type.TryGetInt32(out var kind) ? kind == 0 : null;
                        found[appId] = old with { EnglishName = english, IsGame = game };
                    }
            }
            catch (Exception ex) when (ex is HttpRequestException or JsonException or InvalidDataException or OperationCanceledException) { cancellationToken.ThrowIfCancellationRequested(); }
        }
        cancellationToken.ThrowIfCancellationRequested();
        return found.Values.OrderByDescending(item => SteamArtworkNameMatcher.Score(query, item)).Take(20).ToList();
    }

    private async Task<string?> DownloadAsync(uint appId, CancellationToken lifetime, bool refresh, bool wide = false, bool header = false)
    {
        var suffix = header ? ".header" : wide ? ".capsule" : string.Empty;
        var path = Path.Combine(cacheDirectory, appId + suffix + ".jpg");
        var failed = Path.Combine(cacheDirectory, appId + suffix + ".retry");
        await _downloads.WaitAsync(lifetime).ConfigureAwait(false);
        try
        {
            if (!refresh && ValidCache(path)) return path;
            if (!refresh && File.Exists(failed)
                && _clock.GetUtcNow().UtcDateTime - File.GetLastWriteTimeUtc(failed) < TimeSpan.FromMinutes(5)) return null;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(lifetime);
            timeout.CancelAfter(TimeSpan.FromSeconds(18));
            var urls = new List<string>();
            try
            {
                var input = JsonSerializer.Serialize(new
                {
                    ids = new[] { new { appid = appId } },
                    context = new { language = "english", country_code = "US" },
                    data_request = new { include_assets = true }
                });
                using var json = JsonDocument.Parse(await ReadAsync(
                    "https://api.steampowered.com/IStoreBrowseService/GetItems/v1/?input_json=" + Uri.EscapeDataString(input),
                    512 * 1024, timeout.Token));
                if (json.RootElement.ValueKind == JsonValueKind.Object && json.RootElement.TryGetProperty("response", out var response) && response.ValueKind == JsonValueKind.Object
                    && response.TryGetProperty("store_items", out var items) && items.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in items.EnumerateArray())
                    {
                        if (item.ValueKind != JsonValueKind.Object || !item.TryGetProperty("appid", out var id)
                            || id.ValueKind != JsonValueKind.Number || !id.TryGetUInt32(out var number) || number != appId
                            || !item.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Object) continue;
                        foreach (var key in header ? new[] { "header" } : wide ? new[] { "main_capsule" } : new[] { "library_capsule_2x", "library_capsule" })
                            if (TryAssetUrl(appId, assets, key) is { } url) urls.Add(url);
                    }
                }
            }
            catch (Exception ex) when (ex is HttpRequestException or JsonException or InvalidDataException) { }
            // Public metadata and older library art do not always become available together.
            if (wide) urls.Add($"https://shared.steamstatic.com/store_item_assets/steam/apps/{appId}/{(header ? "header.jpg" : "capsule_616x353.jpg")}");
            else
            {
                urls.Add($"https://shared.steamstatic.com/store_item_assets/steam/apps/{appId}/library_600x900_2x.jpg");
                urls.Add($"https://shared.steamstatic.com/store_item_assets/steam/apps/{appId}/library_600x900.jpg");
            }
            foreach (var url in urls.Distinct(StringComparer.Ordinal))
            {
                try
                {
                    var bytes = await ReadAsync(url, ImageLimit, timeout.Token, image: true);
                    if (!validateImage(bytes)) continue;
                    Directory.CreateDirectory(cacheDirectory);
                    var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
                    try
                    {
                        await File.WriteAllBytesAsync(temporary, bytes, timeout.Token);
                        File.Move(temporary, path, true);
                    }
                    finally { if (File.Exists(temporary)) File.Delete(temporary); }
                    if (File.Exists(failed)) File.Delete(failed);
                    return path;
                }
                catch (Exception ex) when (ex is HttpRequestException or InvalidDataException) { }
            }
            WriteRetry(failed);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or UnauthorizedAccessException or JsonException or OperationCanceledException)
        {
            if (!lifetime.IsCancellationRequested) WriteRetry(failed);
        }
        finally { _downloads.Release(); }
        return ValidCache(path) ? path : null;
    }

    private bool ValidCache(string path)
    {
        try { return File.Exists(path) && new FileInfo(path).Length <= ImageLimit && validateImage(File.ReadAllBytes(path)); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }

    private void WriteRetry(string path)
    {
        try
        {
            Directory.CreateDirectory(cacheDirectory);
            File.WriteAllText(path, string.Empty);
            File.SetLastWriteTimeUtc(path, _clock.GetUtcNow().UtcDateTime);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    internal static string? TryAssetUrl(uint appId, JsonElement assets, string key)
    {
        if (assets.ValueKind != JsonValueKind.Object || !assets.TryGetProperty("asset_url_format", out var format) || format.ValueKind != JsonValueKind.String
            || !assets.TryGetProperty(key, out var filename) || filename.ValueKind != JsonValueKind.String) return null;
        var template = format.GetString();
        var file = filename.GetString();
        if (template is null || file is null || !template.StartsWith($"steam/apps/{appId}/", StringComparison.Ordinal)
            || !template.Contains("${FILENAME}", StringComparison.Ordinal) || file.Contains("..", StringComparison.Ordinal)
            || file.Any(character => !(char.IsAsciiLetterOrDigit(character) || character is '/' or '_' or '-' or '.'))) return null;
        var uri = new Uri("https://shared.steamstatic.com/store_item_assets/" + template.Replace("${FILENAME}", file, StringComparison.Ordinal));
        return uri.Host == "shared.steamstatic.com" && uri.AbsolutePath.StartsWith($"/store_item_assets/steam/apps/{appId}/", StringComparison.Ordinal)
            ? uri.AbsoluteUri : null;
    }

    private async Task<byte[]> ReadAsync(string url, int limit, CancellationToken cancellationToken, bool image = false)
    {
        using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        if (image && response.Content.Headers.ContentType?.MediaType is not ("image/jpeg" or "image/png"))
            throw new InvalidDataException("Steam returned a non-image response.");
        if (response.Content.Headers.ContentLength > limit) throw new InvalidDataException("Steam response exceeds the size limit.");
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var output = new MemoryStream();
        var buffer = new byte[16384];
        int read;
        while ((read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
        {
            if (output.Length + read > limit) throw new InvalidDataException("Steam response exceeds the size limit.");
            output.Write(buffer, 0, read);
        }
        return output.ToArray();
    }
}
