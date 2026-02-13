using System;
using System.Threading;
using System.Threading.Tasks;
using GameHelper.ConsoleHost.Models;
using GameHelper.ConsoleHost.Services;
using GameHelper.Core.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace GameHelper.Tests;

public sealed class FileDropRequestHandlerTests
{
    [Fact]
    public async Task HandleAsync_ValidRequest_ReturnsSummaryAndReloads()
    {
        var processor = new FakeProcessor
        {
            LooksLikeFilePathsResult = true,
            Summary = new AddSummary { Added = 1, Updated = 2, Skipped = 3, DuplicatesRemoved = 4, ConfigPath = "config.yml" }
        };
        var automation = new FakeAutomationService();
        var services = new ServiceCollection().BuildServiceProvider();
        var handler = new FileDropRequestHandler(services, processor, automation, NullLogger<FileDropRequestHandler>.Instance);

        var result = await handler.HandleAsync(new DropAddRequest { Paths = new[] { @"C:\game.exe" } }, CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal(1, result.Added);
        Assert.Equal(2, result.Updated);
        Assert.Equal(3, result.Skipped);
        Assert.Equal(4, result.DuplicatesRemoved);
        Assert.Equal("config.yml", result.ConfigPath);
        Assert.Equal(1, automation.ReloadCalls);
    }

    [Fact]
    public async Task HandleAsync_InvalidPaths_ReturnsError()
    {
        var processor = new FakeProcessor { LooksLikeFilePathsResult = false };
        var automation = new FakeAutomationService();
        var services = new ServiceCollection().BuildServiceProvider();
        var handler = new FileDropRequestHandler(services, processor, automation, NullLogger<FileDropRequestHandler>.Instance);

        var result = await handler.HandleAsync(new DropAddRequest { Paths = new[] { @"C:\not-valid.txt" } }, CancellationToken.None);

        Assert.False(result.Success);
        Assert.NotNull(result.Error);
        Assert.Equal(0, automation.ReloadCalls);
    }

    [Fact]
    public async Task HandleAsync_ConcurrentRequests_AreSerialized()
    {
        var processor = new FakeProcessor
        {
            LooksLikeFilePathsResult = true,
            Summary = new AddSummary { Added = 1, ConfigPath = "config.yml" },
            DelayMs = 120
        };
        var automation = new FakeAutomationService();
        var services = new ServiceCollection().BuildServiceProvider();
        var handler = new FileDropRequestHandler(services, processor, automation, NullLogger<FileDropRequestHandler>.Instance);

        var t1 = handler.HandleAsync(new DropAddRequest { Paths = new[] { @"C:\a.exe" } }, CancellationToken.None);
        var t2 = handler.HandleAsync(new DropAddRequest { Paths = new[] { @"C:\b.exe" } }, CancellationToken.None);

        await Task.WhenAll(t1, t2);

        Assert.False(processor.SawParallelExecution);
        Assert.Equal(2, automation.ReloadCalls);
    }

    private sealed class FakeProcessor : IFileDropProcessor
    {
        private int _activeCalls;

        public bool LooksLikeFilePathsResult { get; set; }

        public bool SawParallelExecution { get; private set; }

        public int DelayMs { get; set; }

        public AddSummary Summary { get; set; } = new();

        public bool LooksLikeFilePaths(string[] paths) => LooksLikeFilePathsResult;

        public AddSummary ProcessFilePaths(string[] paths, string? configOverride, IServiceProvider services)
        {
            if (Interlocked.Increment(ref _activeCalls) > 1)
            {
                SawParallelExecution = true;
            }

            try
            {
                if (DelayMs > 0)
                {
                    Thread.Sleep(DelayMs);
                }

                return Summary;
            }
            finally
            {
                Interlocked.Decrement(ref _activeCalls);
            }
        }
    }

    private sealed class FakeAutomationService : IGameAutomationService
    {
        public int ReloadCalls { get; private set; }

        public void Start()
        {
        }

        public void ReloadConfig()
        {
            ReloadCalls++;
        }

        public void Stop()
        {
        }
    }
}
