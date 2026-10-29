using System.Text.Json;
using GameHelper.Core.Models;
using GameHelper.Infrastructure.Storage.Sqlite;

namespace GameHelper.Infrastructure.Artwork;

/// <summary>Serial Steam lookup. Failed refreshes retain cached declarations and use exponential backoff.</summary>
public sealed class SteamControllerSupportClient(HttpClient http, SqliteControllerSupportStore store, TimeProvider clock)
{
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static long _lastRequest;
    public static ControllerSupport Parse(string json, uint id)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement.GetProperty(id.ToString(System.Globalization.CultureInfo.InvariantCulture));
        if (!root.GetProperty("success").GetBoolean()) throw new HttpRequestException("Steam appdetails success=false");
        var data = root.GetProperty("data");
        var categories = data.TryGetProperty("categories", out var cats) ? cats.EnumerateArray().Select(c => c.GetProperty("id").GetInt32()).ToHashSet() : [];
        var field = data.TryGetProperty("controller_support", out var support) ? support.GetString() : null;
        var status = field == "full" || categories.Contains(28) ? "full" : field == "partial" || categories.Contains(18) ? "partial" : "unlisted";
        return new(status, categories.Contains(55) || categories.Contains(56), categories.Contains(57) || categories.Contains(58));
    }
    public async Task<ControllerSupport> GetAsync(uint id, bool network, CancellationToken token)
    {
        var cached = store.Read(id);
        if (!network || cached?.RetryAt > clock.GetUtcNow()) return cached?.Value ?? new();
        await Gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            cached = store.Read(id);
            if (cached?.RetryAt > clock.GetUtcNow()) return cached.Value.Value;
            var elapsed = System.Diagnostics.Stopwatch.GetElapsedTime(_lastRequest);
            if (elapsed < TimeSpan.FromSeconds(1.5)) await Task.Delay(TimeSpan.FromSeconds(1.5) - elapsed, token).ConfigureAwait(false);
            ControllerSupport value;
            try
            {
                _lastRequest = System.Diagnostics.Stopwatch.GetTimestamp();
                value = Parse(await http.GetStringAsync($"https://store.steampowered.com/api/appdetails?appids={id}&l=english", token).ConfigureAwait(false), id);
            }
            catch (Exception ex) when (ex is HttpRequestException or JsonException or KeyNotFoundException or InvalidOperationException || ex is OperationCanceledException && !token.IsCancellationRequested)
            {
                var failures = Math.Min((cached?.Failures ?? 0) + 1, 11);
                value = cached?.Value ?? new();
                store.Save(id, value, clock.GetUtcNow().AddMinutes(Math.Min(1440, Math.Pow(2, failures))), failures);
                return value;
            }
            store.Save(id, value, clock.GetUtcNow().AddDays(30), 0);
            return value;
        }
        finally { Gate.Release(); }
    }
}
