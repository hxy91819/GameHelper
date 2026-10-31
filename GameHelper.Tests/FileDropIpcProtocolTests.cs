using System.Text;
using System.IO.Pipes;
using GameHelper.Core.Abstractions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using GameHelper.ConsoleHost.Services;

namespace GameHelper.Tests;

public sealed class FileDropIpcProtocolTests
{
    [WindowsOnlyFact]
    public async Task ExpiredClientDoesNotStopServerOrBlockNextClient()
    {
        var clock = new DeadlineClock();
        var name = "gh-ipc-test-" + Guid.NewGuid().ToString("N");
        var intake = new FileDropIntake(Mock.Of<IGameCatalogService>(), Mock.Of<ISteamGameResolver>(),
            Mock.Of<IGameAutomationService>(), Mock.Of<IConfigPathProvider>(), NullLogger<FileDropIntake>.Instance);
        using var server = new FileDropIpcServer(intake, NullLogger<FileDropIpcServer>.Instance, name, clock);
        await server.StartAsync(default);
        try
        {
            using var silent = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
            await silent.ConnectAsync();
            await clock.Created.Task;
            clock.Expire();
            using var next = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
            await next.ConnectAsync();
            using var writer = new StreamWriter(next) { AutoFlush = true };
            await writer.WriteLineAsync("{\"paths\":[]}");
            var response = await FileDropIpcProtocol.ReadLineAsync(next, default);
            Assert.Contains("Invalid drag-drop payload", response);
        }
        finally { await server.StopAsync(default); }
    }

    [Fact]
    public async Task SilentClientReadEndsAtDeadlineWithoutWallClockDelay()
    {
        var clock = new DeadlineClock();
        using var stream = new SilentStream();
        using var reader = new StreamReader(stream);
        var read = FileDropIpcProtocol.ReadLineAsync(stream, default, clock);
        Assert.False(read.IsCompleted);
        clock.Expire();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => read);
    }

    private sealed class DeadlineClock : TimeProvider
    {
        private TimerCallback? callback;
        private object? state;
        internal TaskCompletionSource Created { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            this.callback = callback; this.state = state;
            Created.TrySetResult();
            return new Timer(_ => { }, null, Timeout.Infinite, Timeout.Infinite);
        }
        internal void Expire() => callback!(state);
    }

    private sealed class SilentStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        { await Task.Delay(Timeout.Infinite, cancellationToken); return 0; }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    [Fact]
    public async Task OversizedUnterminatedRequestIsRejectedWithoutWaitingForNewline()
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(new string('x', 1024 * 1024 + 1)));
        using var reader = new StreamReader(stream);
        await Assert.ThrowsAsync<InvalidDataException>(() => FileDropIpcProtocol.ReadLineAsync(stream, default));
    }

    [Theory]
    [InlineData("{\"paths\":[]}\r\n", "{\"paths\":[]}")]
    [InlineData("{\"paths\":[]}\n", "{\"paths\":[]}")]
    [InlineData("", null)]
    public async Task ExistingLineFormatIsPreserved(string input, string? expected)
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(input));
        using var reader = new StreamReader(stream);
        Assert.Equal(expected, await FileDropIpcProtocol.ReadLineAsync(stream, default));
    }
}

