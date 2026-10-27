using GameHelper.Core.Utilities;

namespace GameHelper.Tests;

public sealed class AtomicFileTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));

    public AtomicFileTests() => Directory.CreateDirectory(_root);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Write_PublishesCompleteContent(bool existing)
    {
        var path = Path.Combine(_root, "document");
        if (existing) File.WriteAllText(path, "old");
        AtomicFile.Write(path, temporary => File.WriteAllText(temporary, "new"));
        Assert.Equal("new", File.ReadAllText(path));
        Assert.Empty(Directory.GetFiles(_root, "*.tmp"));
    }

    [Fact]
    public void Write_WriterFailurePreservesTargetAndCleansPartialFile()
    {
        var path = Path.Combine(_root, "document");
        File.WriteAllText(path, "old");
        Assert.Throws<InvalidOperationException>(() => AtomicFile.Write(path, temporary =>
        {
            File.WriteAllText(temporary, "partial");
            throw new InvalidOperationException("write failed");
        }));
        Assert.Equal("old", File.ReadAllText(path));
        Assert.Empty(Directory.GetFiles(_root, "*.tmp"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WriteAsync_PublishesCompleteContent(bool existing)
    {
        var path = Path.Combine(_root, "document");
        if (existing) File.WriteAllText(path, "old");
        await AtomicFile.WriteAsync(path, temporary => File.WriteAllTextAsync(temporary, "new"));
        Assert.Equal("new", File.ReadAllText(path));
        Assert.Empty(Directory.GetFiles(_root, "*.tmp"));
    }

    [Fact]
    public async Task WriteAsync_WriterFailurePreservesTargetAndCleansPartialFile()
    {
        var path = Path.Combine(_root, "document");
        File.WriteAllText(path, "old");
        await Assert.ThrowsAsync<InvalidOperationException>(() => AtomicFile.WriteAsync(path, async temporary =>
        {
            await File.WriteAllTextAsync(temporary, "partial");
            throw new InvalidOperationException("write failed");
        }));
        Assert.Equal("old", File.ReadAllText(path));
        Assert.Empty(Directory.GetFiles(_root, "*.tmp"));
    }

    public void Dispose() => Directory.Delete(_root, true);
}
