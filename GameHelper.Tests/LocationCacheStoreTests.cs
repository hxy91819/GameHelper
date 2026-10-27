using System;
using System.IO;
using GameHelper.Core.Abstractions;
using GameHelper.Core.Models;
using GameHelper.Infrastructure.Providers;
using Xunit;

namespace GameHelper.Tests;

public class LocationCacheStoreTests : IDisposable
{
    private static readonly DateTimeOffset FixedNow = new(2026, 10, 17, 9, 0, 0, TimeSpan.Zero);

    private readonly string _tempDir;

    public LocationCacheStoreTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "GameHelper.Tests." + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        Directory.Delete(_tempDir, recursive: true);
    }

    private string CachePath => Path.Combine(_tempDir, "location-cache.json");

    [Fact]
    public void SaveThenLoad_RoundTripsCoordinate()
    {
        var store = new JsonLocationCacheStore(CachePath, timeProvider: new FixedClock(FixedNow));

        store.Save(new UserCoordinate(39.907, 116.391));
        var cached = store.Load();

        Assert.NotNull(cached);
        Assert.Equal(39.907, cached!.Latitude, precision: 6);
        Assert.Equal(116.391, cached.Longitude, precision: 6);
        Assert.Equal(FixedNow.DateTime, cached.UpdatedAt);
        Assert.False(File.Exists(CachePath + ".tmp"));
    }

    [Fact]
    public void Load_MissingFile_ReturnsNull()
    {
        var store = new JsonLocationCacheStore(CachePath);

        Assert.Null(store.Load());
    }

    [Fact]
    public void Load_CorruptFile_ReturnsNullInsteadOfThrowing()
    {
        File.WriteAllText(CachePath, "{ nope");
        var store = new JsonLocationCacheStore(CachePath);

        Assert.Null(store.Load());
    }

    [Fact]
    public void Load_OutOfRangeValues_Rejected()
    {
        File.WriteAllText(CachePath, "{\"Latitude\":123,\"Longitude\":-999,\"UpdatedAt\":\"2026-10-17T09:00:00\"}");
        var store = new JsonLocationCacheStore(CachePath);

        Assert.Null(store.Load());
    }

    [Fact]
    public void Save_OverwritesPreviousValue()
    {
        var store = new JsonLocationCacheStore(CachePath);

        store.Save(new UserCoordinate(1, 2));
        store.Save(new UserCoordinate(3, 4));

        var cached = store.Load();
        Assert.NotNull(cached);
        Assert.Equal(3, cached!.Latitude, precision: 6);
        Assert.Equal(4, cached.Longitude, precision: 6);
    }

    private sealed class FixedClock(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;

        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
    }
}