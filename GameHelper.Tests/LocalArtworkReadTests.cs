using GameHelper.Desktop;

namespace GameHelper.Tests;

public sealed class LocalArtworkReadTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void BoundedReadAcceptsFilesAtOrBelowExistingLimit(int offset)
    {
        using var stream = new GeneratedStream(LocalGameArtwork.MaximumImageBytes + offset);
        var bytes = LocalGameArtwork.ReadImageBytes(stream);
        Assert.NotNull(bytes);
        Assert.Equal(LocalGameArtwork.MaximumImageBytes + offset, bytes.Length);
    }

    [Fact]
    public void NonSeekableOrGrowingInputStopsAfterLimitPlusOneByte()
    {
        using var stream = new GeneratedStream(long.MaxValue);
        Assert.Null(LocalGameArtwork.ReadImageBytes(stream));
        Assert.Equal(LocalGameArtwork.MaximumImageBytes + 1L, stream.BytesRead);
    }

    [Fact]
    public void OversizedLocalFileIsRejectedBeforeReadingItsContents()
    {
        var path = Path.Combine(Path.GetTempPath(), "gh-oversized-" + Guid.NewGuid().ToString("N") + ".png");
        try
        {
            using (var file = File.Create(path)) file.SetLength(LocalGameArtwork.MaximumImageBytes + 1L);
            Assert.Null(LocalGameArtwork.LoadPoster(path));
            Assert.Null(LocalGameArtwork.LoadBanner(path));
        }
        finally { File.Delete(path); }
    }

    private sealed class GeneratedStream(long size) : Stream
    {
        public long BytesRead { get; private set; }
        public override int Read(byte[] buffer, int offset, int count)
        {
            var read = (int)Math.Min(count, size - BytesRead);
            Array.Clear(buffer, offset, read); BytesRead += read; return read;
        }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
