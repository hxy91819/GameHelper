using System;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using GameHelper.Core.Abstractions;
using GameHelper.Core.Models;
using Microsoft.Extensions.Logging;

namespace GameHelper.Infrastructure.Providers;

/// <summary>
/// JSON file-backed cache for the last successfully auto-detected coordinate
/// (location-cache.json in the data directory). Writes are atomic.
/// </summary>
public sealed class JsonLocationCacheStore : ILocationCacheStore
{
    private readonly string _filePath;
    private readonly ILogger<JsonLocationCacheStore>? _logger;
    private readonly TimeProvider _timeProvider;

    private static readonly JsonSerializerOptions SerializerOptions = new() { WriteIndented = true };

    public JsonLocationCacheStore(string filePath, ILogger<JsonLocationCacheStore>? logger = null, TimeProvider? timeProvider = null)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            throw new ArgumentException("filePath required", nameof(filePath));
        }

        _filePath = filePath;
        _logger = logger;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <inheritdoc />
    public CachedLocation? Load()
    {
        try
        {
            if (!File.Exists(_filePath))
            {
                return null;
            }

            var json = File.ReadAllText(_filePath, Encoding.UTF8);
            var cached = JsonSerializer.Deserialize<CachedLocation>(json);
            if (cached is null
                || double.IsNaN(cached.Latitude) || double.IsNaN(cached.Longitude)
                || Math.Abs(cached.Latitude) > 90 || Math.Abs(cached.Longitude) > 180)
            {
                _logger?.LogWarning("Location cache at {Path} holds out-of-range or unreadable values; ignoring it.", _filePath);
                return null;
            }

            return cached;
        }
        catch (Exception ex) when (ex is IOException or JsonException or ArgumentException)
        {
            _logger?.LogWarning(ex, "Location cache at {Path} is unreadable; ignoring it.", _filePath);
            return null;
        }
    }

    /// <inheritdoc />
    public void Save(UserCoordinate coordinate)
    {
        var directory = Path.GetDirectoryName(_filePath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var cached = new CachedLocation
        {
            Latitude = coordinate.Latitude,
            Longitude = coordinate.Longitude,
            UpdatedAt = _timeProvider.GetLocalNow().DateTime
        };

        var tempPath = _filePath + ".tmp";
        try
        {
            File.WriteAllText(tempPath, JsonSerializer.Serialize(cached, SerializerOptions), Encoding.UTF8);
            File.Move(tempPath, _filePath, overwrite: true);
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                File.Delete(tempPath);
            }
        }
    }
}