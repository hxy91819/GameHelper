using System.Net.Http;
using System.Text.Json;

namespace GameHelper.Infrastructure.Artwork;

/// <summary>Best-effort Steam store genres with bounded, serialized requests and a persistent stale cache.</summary>
public sealed class SteamGenreClient(HttpClient http, string cacheDirectory, TimeProvider? timeProvider = null)
{
    private readonly SemaphoreSlim _gate = new(1);
    private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;
    private DateTimeOffset _nextRequest;
    private sealed record Cache(DateTimeOffset AttemptedAt, DateTimeOffset? SucceededAt, string[] Genres);

    public async Task<IReadOnlyList<string>> GetAsync(uint appId, bool allowNetwork, CancellationToken cancellationToken = default)
    {
        if (appId == 0) return [];
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var path = Path.Combine(cacheDirectory, appId + ".json");
            Cache? cached = null;
            try
            {
                if (File.Exists(path) && new FileInfo(path).Length <= 262144)
                    cached = JsonSerializer.Deserialize<Cache>(await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { }
            var now = _clock.GetUtcNow();
            if (!allowNetwork || cached is not null && (now - cached.AttemptedAt < TimeSpan.FromHours(6)
                || cached.SucceededAt is { } success && now - success < TimeSpan.FromDays(30))) return cached?.Genres ?? [];
            var wait = _nextRequest - now;
            if (wait > TimeSpan.Zero) await Task.Delay(wait, _clock, cancellationToken).ConfigureAwait(false);
            string[]? genres = null;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(12));
            var requestToken = timeout.Token;
            try
            {
                using var response = await http.GetAsync($"https://store.steampowered.com/api/appdetails?appids={appId}&l=schinese&filters=genres",
                    HttpCompletionOption.ResponseHeadersRead, requestToken).ConfigureAwait(false);
                response.EnsureSuccessStatusCode();
                if (response.Content.Headers.ContentLength is > 262144) throw new IOException("Steam metadata exceeds size limit.");
                await using var stream = await response.Content.ReadAsStreamAsync(requestToken).ConfigureAwait(false);
                using var buffer = new MemoryStream();
                var chunk = new byte[8192];
                int count;
                while ((count = await stream.ReadAsync(chunk, requestToken).ConfigureAwait(false)) > 0)
                {
                    if (buffer.Length + count > 262144) throw new IOException("Steam metadata exceeds size limit.");
                    buffer.Write(chunk, 0, count);
                }
                using var json = JsonDocument.Parse(buffer.ToArray());
                var app = json.RootElement.GetProperty(appId.ToString(System.Globalization.CultureInfo.InvariantCulture));
                if (!app.GetProperty("success").GetBoolean()) throw new IOException("Steam metadata unavailable.");
                genres = app.GetProperty("data").TryGetProperty("genres", out var values)
                    ? Normalize(values.EnumerateArray().Select(value => value.GetProperty("description").GetString() ?? "")) : [];
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception ex) when (ex is HttpRequestException or IOException or JsonException or KeyNotFoundException or InvalidOperationException or OperationCanceledException) { }
            finally { _nextRequest = _clock.GetUtcNow().AddSeconds(1); }
            var updated = new Cache(_clock.GetUtcNow(), genres is null ? cached?.SucceededAt : _clock.GetUtcNow(), genres ?? cached?.Genres ?? []);
            var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                Directory.CreateDirectory(cacheDirectory);
                await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(updated), cancellationToken).ConfigureAwait(false);
                File.Move(temporary, path, true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            finally { try { if (File.Exists(temporary)) File.Delete(temporary); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
            return updated.Genres;
        }
        finally { _gate.Release(); }
    }

    public static string[] Normalize(IEnumerable<string> genres) => genres.Select(value => value.Trim()).Where(value => value.Length is > 0 and <= 40)
        .Distinct(StringComparer.OrdinalIgnoreCase).Take(20).ToArray();
}
