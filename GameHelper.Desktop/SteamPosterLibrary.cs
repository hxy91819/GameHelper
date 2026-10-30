using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Windows.Media;
using GameHelper.Core.Utilities;
using GameHelper.Infrastructure.Artwork;
using Microsoft.Win32;

namespace GameHelper.Desktop;

internal sealed record GameArtwork(ImageSource? Image, bool IsCover, string Description);

/// <summary>Artwork belongs to the desktop; associations never change launch or automation settings.</summary>
internal sealed class SteamPosterLibrary
{
    private static readonly HttpClient Http = new(new SocketsHttpHandler { AllowAutoRedirect = false })
    { Timeout = TimeSpan.FromSeconds(20) };
    private readonly SteamArtworkClient _client;
    private readonly string? _linksPath;
    private readonly string? _coversDirectory;
    private readonly bool _allowNetwork;
    private Dictionary<string, uint> _links;
    private readonly DesktopDocument<Dictionary<string, uint>> _document;
    private readonly Func<uint, string?> _localPoster;

    public SteamPosterLibrary(string? configPath, bool allowNetwork, IDesktopDocumentReader? reader = null) : this(configPath, allowNetwork,
        new SteamArtworkClient(Http, Path.Combine(AppDataPath.GetGameHelperDirectory(), "cache", "steam-posters"), LocalGameArtwork.IsValidPoster), LocalPoster, reader,
        Path.Combine(AppDataPath.GetGameHelperDirectory(), "covers")) { }

    internal SteamPosterLibrary(string? configPath, bool allowNetwork, SteamArtworkClient client, Func<uint, string?> localPoster, IDesktopDocumentReader? reader = null,
        string? coversDirectory = null)
    {
        _allowNetwork = allowNetwork;
        _linksPath = configPath is null ? null : configPath + ".desktop-artwork.json";
        _client = client;
        _localPoster = localPoster;
        _coversDirectory = string.IsNullOrWhiteSpace(coversDirectory) ? null : coversDirectory;
        _document = new(_linksPath, () => new(), reader: reader);
        var saved = _document.Read();
        _links = _document.UsesSqlite ? new(saved, StringComparer.OrdinalIgnoreCase) : saved;
    }

    internal bool AllowNetwork => _allowNetwork;
    internal uint? AssociatedAppId(string dataKey) => _links.TryGetValue(dataKey, out var linked) ? linked : null;

    public Task<IReadOnlyList<SteamArtworkGame>> SearchAsync(string query, CancellationToken cancellationToken) => _client.SearchAsync(query, cancellationToken);
    internal bool HasAssociation(string dataKey) => _links.ContainsKey(dataKey);
    internal async Task<bool> MatchNameAsync(string dataKey, string title, CancellationToken cancellationToken)
    {
        if (HasAssociation(dataKey)) return false;
        var found = await SearchAsync(title, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        if (HasAssociation(dataKey) || SteamArtworkNameMatcher.BestMatch(title, found) is not { } exact) return false;
        await Task.Run(() => Associate(dataKey, exact.AppId), cancellationToken);
        return true;
    }
    internal async Task<ImageSource?> PreviewAsync(uint appId, CancellationToken cancellationToken)
    {
        var path = await _client.GetPosterAsync(appId, cancellationToken);
        return path is null ? null : await Task.Run(() => LocalGameArtwork.LoadPoster(path), cancellationToken);
    }

    public void Associate(string dataKey, uint? appId)
    {
        lock (_saveGate) SaveCore(dataKey, appId);
    }
    // Order: _saveGate -> SQLite writer; release writer before taking the projection admission gate.
    // The worker releases admission before directory lease -> SQLite writer, and never takes _saveGate.
    // Never take this gate under a SQLite transaction, directory lease or projection gate. Storage/queue admission must not
    // synchronously call back into Associate/this store; no UI dispatch, network, or user callbacks under the gate.
    private readonly object _saveGate = new();
    private void SaveCore(string dataKey, uint? appId)
    {
        if (_linksPath is null) throw new InvalidOperationException("此只读界面不能保存海报关联。");
        var next = new Dictionary<string, uint>(_links, StringComparer.OrdinalIgnoreCase);
        if (appId is > 0) next[dataKey] = appId.Value;
        else next.Remove(dataKey);
        _document.Write(next);
        _links = next;
    }

    public async Task<GameArtwork> LoadAsync(string dataKey, string executable, string target, CancellationToken cancellationToken, bool refresh)
    {
        uint? id = _links.TryGetValue(dataKey, out var linked) ? linked : null;
        id ??= await Task.Run(() => SteamArtworkIdentity.Resolve(executable, target), cancellationToken);
        var fallback = await Task.Run(() => LocalGameArtwork.LoadIcon(executable), cancellationToken);
        if (id is null)
        {
            // Games without a Steam page (itch.io and friends) show a locally placed covers/<dataKey> file; a Steam id, once known, keeps outranking it.
            var cover = await Task.Run(() => TryLocalCover(dataKey) is { } file ? LocalGameArtwork.LoadPoster(file) : null, cancellationToken);
            return cover is not null ? new GameArtwork(cover, true, "本地封面") : new GameArtwork(fallback, false, "尚未关联 Steam 海报");
        }
        try
        {
            var cached = await Task.Run(() => _client.GetCachedPoster(id.Value) ?? _localPoster(id.Value), cancellationToken);
            if (!refresh && cached is not null && await Task.Run(() => LocalGameArtwork.LoadPoster(cached), cancellationToken) is { } local)
                return new GameArtwork(local, true, $"Steam 海报 · {id}");
            var downloaded = _allowNetwork ? await _client.GetPosterAsync(id.Value, cancellationToken, refresh) : cached;
            downloaded ??= cached;
            if (downloaded is not null && await Task.Run(() => LocalGameArtwork.LoadPoster(downloaded), cancellationToken) is { } poster)
                return new GameArtwork(poster, true, $"Steam 海报 · {id}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or HttpRequestException or JsonException or OperationCanceledException) { }
        return new GameArtwork(fallback, false, $"Steam {id} 海报暂不可用，可重新匹配后重试");
    }

    internal async Task<ImageSource?> LoadBannerAsync(string dataKey, string executable, string target, CancellationToken cancellationToken, bool header = false, bool refresh = false)
    {
        var linked = AssociatedAppId(dataKey);
        var id = linked ?? await Task.Run(() => SteamArtworkIdentity.Resolve(executable, target), cancellationToken);
        if (id is null)
        {
            // A landscape local cover can stand in for the wide hero/detail/backdrop slots; portrait files stay out of wide slots.
            return await Task.Run(() => TryLocalCover(dataKey) is { } file && LocalGameArtwork.IsWideBannerImage(file) ? (ImageSource?)LocalGameArtwork.LoadBanner(file) : null, cancellationToken);
        }
        var path = await Task.Run(() => (header ? _client.GetCachedHeader(id.Value) : _client.GetCachedCapsule(id.Value)), cancellationToken);
        if ((path is null || refresh) && _allowNetwork) path = header ? await _client.GetHeaderAsync(id.Value, cancellationToken, refresh) : await _client.GetCapsuleAsync(id.Value, cancellationToken, refresh);
        return path is null ? null : await Task.Run(() => LocalGameArtwork.LoadBanner(path), cancellationToken);
    }

    private static string? LocalPoster(uint appId)
    {
        var steam = Environment.GetEnvironmentVariable("GAMEHELPER_STEAM_ROOT")
            ?? Registry.GetValue(@"HKEY_CURRENT_USER\Software\Valve\Steam", "SteamPath", null) as string;
        if (steam is null) return null;
        var cache = Path.Combine(steam, "appcache", "librarycache");
        var older = Path.Combine(cache, appId + "_library_600x900.jpg");
        if (File.Exists(older) && LocalGameArtwork.LoadPoster(older) is not null) return older;
        var modern = Path.Combine(cache, appId.ToString());
        if (!Directory.Exists(modern)) return null;
        return Directory.EnumerateFiles(modern, "*library_600x900*.jpg", SearchOption.TopDirectoryOnly)
            .FirstOrDefault(path => LocalGameArtwork.LoadPoster(path) is not null);
    }

    /// <summary>Locates covers/&lt;dataKey&gt;.{jpg,jpeg,png}; dataKey is a plain slug (letters, digits, '-' and '_') to keep the folder scoped.</summary>
    private string? TryLocalCover(string dataKey)
    {
        if (_coversDirectory is null || !dataKey.All(item => char.IsLetterOrDigit(item) || item is '-' or '_')) return null;
        return new[] { "jpg", "jpeg", "png" }.Select(ext => Path.Combine(_coversDirectory, dataKey + "." + ext))
            .FirstOrDefault(File.Exists);
    }
}
