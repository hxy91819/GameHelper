using System.Text.Json;
using GameHelper.Desktop;

namespace GameHelper.Tests;

public sealed class DesktopDocumentStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
    private string PathFor(string name) => Path.Combine(_root, name);

    [Theory]
    [InlineData("missing")]
    [InlineData("corrupt")]
    [InlineData("null")]
    public void Read_UnavailableDocumentFallsBackToEmpty(string kind)
    {
        Directory.CreateDirectory(_root);
        var path = PathFor("side.json");
        if (kind != "missing") File.WriteAllText(path, kind == "null" ? "null" : "{broken");
        Assert.Empty(DesktopDocumentStore.Read(path, () => new List<string>()));
    }

    [Fact]
    public void Read_StrictDraftPolicyPreservesReadFailure()
    {
        Directory.CreateDirectory(_root);
        var path = PathFor("side.json");
        File.WriteAllText(path, "{broken");
        Assert.Throws<JsonException>(() =>
            DesktopDocumentStore.Read(path, () => new List<string>(), recoverReadFailure: false));
    }

    [Fact]
    public void Write_CreatesDirectoryAndReopensUnchangedJson()
    {
        var path = PathFor("nested/side.json");
        DesktopDocumentStore.Write(path, new[] { "目录", "second" });
        Assert.Equal(JsonSerializer.Serialize(new[] { "目录", "second" }), File.ReadAllText(path));
        DesktopDocumentStore.Write(path, new[] { "replacement" });
        Assert.Equal(new[] { "replacement" }, DesktopDocumentStore.Read(path, () => Array.Empty<string>()));
        Assert.Empty(Directory.GetFiles(_root, "*.tmp", SearchOption.AllDirectories));
    }

    [Fact]
    public void ScanMemory_ReopensRecentRootsAndIgnoredPrograms()
    {
        var config = PathFor("config.yml");
        var store = new LibraryScanStore(config);
        store.Remember("games");
        store.Remember("other");
        store.SetIgnored(["games/tool.exe"], true);
        var reopened = new LibraryScanStore(config);
        Assert.Equal(new[] { "other", "games" }, reopened.Directories);
        Assert.Equal(new[] { "games/tool.exe" }, reopened.Ignored);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }
}
