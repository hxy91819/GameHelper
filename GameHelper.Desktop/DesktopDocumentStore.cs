using System.IO;
using System.Text.Json;
using GameHelper.Core.Utilities;

namespace GameHelper.Desktop;

/// <summary>JSON side documents only; runtime configuration is owned by its configuration provider.</summary>
internal static class DesktopDocumentStore
{
    private static readonly object ProjectionGate = new();
    private static readonly Dictionary<string, ProjectionWork> Projections = new(StringComparer.OrdinalIgnoreCase);
    private sealed class ProjectionWork
    {
        internal bool Pending;
        internal bool Active;
        internal Task Running = Task.CompletedTask;
    }
    internal static void RequestProjection()
    {
        var factory = new GameHelper.Infrastructure.Storage.Sqlite.SqliteConnectionFactory();
        lock (ProjectionGate)
        {
            if (!Projections.TryGetValue(factory.DataDirectory, out var work))
                Projections[factory.DataDirectory] = work = new();
            work.Pending = true;
            if (work.Active) return;
            work.Active = true;
            work.Running = Task.Run(async () =>
            {
                while (true)
                {
                    lock (ProjectionGate) { if (!work.Pending) { work.Active = false; return; } work.Pending = false; }
                    try { await new GameHelper.Infrastructure.Storage.Sqlite.Legacy.SqliteLegacyCompatibility(factory,
                        TimeProvider.System, preserveHistoryKinds: true).ExportAsync(); }
                    catch (Exception ex) { System.Diagnostics.Trace.TraceError("Desktop 兼容投影失败：{0}", ex); }
                }
            });
        }
    }
    internal static Task DrainProjectionsAsync()
    {
        lock (ProjectionGate) return Task.WhenAll(Projections.Values.Select(w => w.Running));
    }

    internal static T Read<T>(string? path, Func<T> empty, bool recoverReadFailure = true) where T : class
    {
        try
        {
            return path is not null && File.Exists(path)
                ? JsonSerializer.Deserialize<T>(File.ReadAllText(path)) ?? empty() : empty();
        }
        catch (Exception ex) when (recoverReadFailure && ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return empty();
        }
    }

    internal static void Write<T>(string path, T document) =>
        WriteJson(path, () => JsonSerializer.Serialize(document));

    // Drafts already serialized their payload for write deduplication and historically use the supplied directory.
    internal static void WriteJson(string path, Func<string> serialize, bool resolveDirectory = true) =>
        AtomicFile.Write(path, temporary =>
        {
            Directory.CreateDirectory(Path.GetDirectoryName(resolveDirectory ? Path.GetFullPath(path) : path)!);
            File.WriteAllText(temporary, serialize());
        });
}

/// <summary>One editor baseline; an activated database never falls back after a read/write failure.</summary>
internal sealed class DesktopDocument<T>(string? path, Func<T> empty, bool recoverReadFailure = true) where T : class
{
    private GameHelper.Infrastructure.Storage.Sqlite.SqliteDesktopStore? _database;
    private GameHelper.Infrastructure.Storage.Sqlite.DesktopDocumentSnapshot? _baseline;
    private string? _kind;
    private bool _read;
    internal T Read()
    {
        _read = true;
        if (path is not null)
        {
            var marker = path.LastIndexOf(".desktop-", StringComparison.Ordinal);
            var factory = new GameHelper.Infrastructure.Storage.Sqlite.SqliteConnectionFactory();
            if (marker >= 0 && File.Exists(factory.DatabasePath))
            {
                // Startup completed the shared initializer and scope import on a worker before creating views.
                if (GameHelper.Infrastructure.Storage.Sqlite.SqliteDesktopStore.IsActivated(factory))
                {
                    _database = new(factory, path[..marker]);
                    _kind = path[(marker + 9)..^5];
                    _baseline = _database.Read(_kind);
                    return JsonSerializer.Deserialize<T>(_baseline.Json) ?? empty();
                }
            }
        }
        return DesktopDocumentStore.Read(path, empty, recoverReadFailure);
    }
    internal bool UsesSqlite { get { if (!_read) Read(); return _database is not null; } }
    internal void Write(T document)
    {
        if (!_read) Read();
        if (_database is not null)
        {
            try { _baseline = _database.Save(_kind!, _baseline!, JsonSerializer.Serialize(document)); }
            catch (Microsoft.Data.Sqlite.SqliteException ex) { throw new IOException("数据库未保存，编辑已保留：" + ex.Message, ex); }
            // Projection failure cannot turn a committed edit into a failed save. Dirty receipt remains for retry.
            DesktopDocumentStore.RequestProjection();
        }
        else DesktopDocumentStore.Write(path ?? throw new InvalidOperationException("只读界面不能保存。"), document);
    }
}
