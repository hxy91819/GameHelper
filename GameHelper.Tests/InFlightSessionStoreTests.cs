using System;
using System.IO;
using System.Linq;
using GameHelper.Core.Models;
using GameHelper.Infrastructure.Providers;
using Xunit;

namespace GameHelper.Tests;

public class InFlightSessionStoreTests : IDisposable
{
    private readonly string _tempDir;

    public InFlightSessionStoreTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "GameHelper.Tests." + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        Directory.Delete(_tempDir, recursive: true);
    }

    private string StorePath => Path.Combine(_tempDir, "inflight-sessions.json");

    private InFlightSnapshot BuildSnapshot(int processId = 4321)
    {
        return new InFlightSnapshot
        {
            SnapshotAt = new DateTime(2026, 10, 17, 10, 0, 0),
            Sessions =
            {
                new InFlightSession
                {
                    DataKey = "witcher3",
                    ProcessId = processId,
                    NormalizedName = "witcher3.exe",
                    NormalizedPath = @"D:\Games\W3\witcher3.exe",
                    StartedAt = new DateTime(2026, 10, 17, 9, 30, 0)
                }
            }
        };
    }

    [Fact]
    public void SaveThenLoad_RoundTripsSnapshot()
    {
        var store = new JsonInFlightSessionStore(StorePath);
        var snapshot = BuildSnapshot();

        store.Save(snapshot);
        var loaded = store.Load();

        Assert.NotNull(loaded);
        Assert.Equal(snapshot.SnapshotAt, loaded!.SnapshotAt);
        var session = Assert.Single(loaded.Sessions);
        Assert.Equal("witcher3", session.DataKey);
        Assert.Equal(4321, session.ProcessId);
        Assert.Equal("witcher3.exe", session.NormalizedName);
        Assert.Equal(@"D:\Games\W3\witcher3.exe", session.NormalizedPath);
        Assert.Equal(snapshot.Sessions[0].StartedAt, session.StartedAt);
    }

    [Fact]
    public void Save_IsAtomic_NoTempFileLeftBehind()
    {
        var store = new JsonInFlightSessionStore(StorePath);

        store.Save(BuildSnapshot());

        Assert.True(File.Exists(StorePath));
        Assert.False(File.Exists(StorePath + ".tmp"));
    }

    [Fact]
    public void Save_StaleTempFileFromAnOldBuild_DoesNotBlockLaterSaves()
    {
        // The pre-hardening build used a fixed "inflight-sessions.json.tmp"; a read-only leftover
        // (or one the current token cannot touch) used to fail every subsequent save.
        var staleTemp = StorePath + ".tmp";
        File.WriteAllText(staleTemp, "stale");
        File.SetAttributes(staleTemp, FileAttributes.ReadOnly);

        try
        {
            var store = new JsonInFlightSessionStore(StorePath);

            store.Save(BuildSnapshot());

            Assert.True(File.Exists(StorePath));
            Assert.NotNull(store.Load());
        }
        finally
        {
            File.SetAttributes(staleTemp, FileAttributes.Normal);
            File.Delete(staleTemp);
        }
    }

    [Fact]
    public void Save_LeavesNoTempFilesBehind()
    {
        var store = new JsonInFlightSessionStore(StorePath);

        store.Save(BuildSnapshot());

        Assert.Empty(Directory.GetFiles(_tempDir, "inflight-sessions.json.*.tmp"));
    }

    [Fact]
    public void Load_MissingFile_ReturnsNull()
    {
        var store = new JsonInFlightSessionStore(StorePath);

        Assert.Null(store.Load());
    }

    [Fact]
    public void Load_CorruptFile_ReturnsNullInsteadOfThrowing()
    {
        File.WriteAllText(StorePath, "{ not valid json ..");
        var store = new JsonInFlightSessionStore(StorePath);

        Assert.Null(store.Load());
    }

    [Fact]
    public void Load_DropsRowsWithoutDataKey()
    {
        var store = new JsonInFlightSessionStore(StorePath);
        var snapshot = BuildSnapshot();
        snapshot.Sessions.Add(new InFlightSession { DataKey = " ", StartedAt = DateTime.Now });
        store.Save(snapshot);

        var loaded = store.Load();

        Assert.NotNull(loaded);
        Assert.Single(loaded!.Sessions);
    }

    [Fact]
    public void Clear_RemovesFile()
    {
        var store = new JsonInFlightSessionStore(StorePath);
        store.Save(BuildSnapshot());

        store.Clear();

        Assert.False(File.Exists(StorePath));
    }

    [Fact]
    public void Clear_AbsentFile_IsNoop()
    {
        var store = new JsonInFlightSessionStore(StorePath);

        store.Clear();

        Assert.False(File.Exists(StorePath));
    }

    [Fact]
    public void Save_EmptySnapshot_ThenLoad_ReturnsEmptySessionList()
    {
        var store = new JsonInFlightSessionStore(StorePath);
        store.Save(new InFlightSnapshot { SnapshotAt = DateTime.Now, Sessions = new() });

        var loaded = store.Load();

        Assert.NotNull(loaded);
        Assert.Empty(loaded!.Sessions);
    }
}