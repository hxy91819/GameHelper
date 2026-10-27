using System.Net;
using System.Text.Json;
using GameHelper.Core.Models;
using GameHelper.Infrastructure.Reports;

namespace GameHelper.Tests;

public sealed class ReportCommentaryClientTests
{
    private static ReportLlmSettings Settings => new() { BaseUrl = "https://example.test/v1/", Model = "model-test", ApiKey = "secret-test" };

    [Fact]
    public async Task GenerateAsync_OnlySendsAllowlistedAggregatesAndAuthHeader()
    {
        string? body = null;
        using var http = new HttpClient(new Handler(async (request, token) =>
        {
            Assert.Equal("https://example.test/v1/chat/completions", request.RequestUri!.AbsoluteUri);
            Assert.Equal("Bearer", request.Headers.Authorization!.Scheme);
            Assert.Equal("secret-test", request.Headers.Authorization.Parameter);
            body = await request.Content!.ReadAsStringAsync(token);
            using var json = JsonDocument.Parse(body);
            Assert.Equal("model-test", json.RootElement.GetProperty("model").GetString());
            using var summary = JsonDocument.Parse(json.RootElement.GetProperty("messages")[1].GetProperty("content").GetString()!);
            Assert.Equal(120, summary.RootElement.GetProperty("Minutes").GetInt64());
            Assert.Equal("game-key", summary.RootElement.GetProperty("topGames")[0].GetProperty("DataKey").GetString());
            return new(HttpStatusCode.OK) { Content = new StringContent("{\"choices\":[{\"message\":{\"content\":\"简短点评\"}}]}") };
        }));
        var result = await new ReportCommentaryClient(http).GenerateAsync(new()
        {
            Minutes = 120, TopGames = [new("game-key", @"C:\Private\DisplayName", 120, 2, new(2026, 1, 1, 14, 32, 0))],
            LongestSession = new("game-key", "private-name", 60, 1, new(2026, 1, 1, 14, 32, 0))
        }, Settings);
        Assert.Equal("简短点评", result);
        foreach (var sensitive in new[] { "Private", "private-name", "LastPlayed", "StartTime", "EndTime", "secret-test", "14:32", "ConfigPath" })
            Assert.DoesNotContain(sensitive, body!);
    }

    [Fact]
    public async Task GenerateAsync_Timeout_UsesInjectedTimerAndDoesNotWaitForWallClock()
    {
        var clock = new ManualClock();
        using var http = new HttpClient(new Handler(async (_, token) =>
        { await Task.Delay(Timeout.Infinite, token); return new(HttpStatusCode.OK); })) { Timeout = Timeout.InfiniteTimeSpan };
        var task = new ReportCommentaryClient(http, clock).GenerateAsync(new(), Settings);
        Assert.Equal(TimeSpan.FromSeconds(60), clock.DueTime);
        clock.Fire();
        var error = await Assert.ThrowsAsync<TimeoutException>(() => task);
        Assert.Contains("60 秒", error.Message);
    }

    [Fact]
    public async Task GenerateAsync_CallerCancellation_PropagatesCancellation()
    {
        using var cancel = new CancellationTokenSource();
        using var http = new HttpClient(new Handler(async (_, token) =>
        { await Task.Delay(Timeout.Infinite, token); return new(HttpStatusCode.OK); }));
        var task = new ReportCommentaryClient(http).GenerateAsync(new(), Settings, cancel.Token);
        await cancel.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
    }

    [Theory]
    [InlineData(401)] [InlineData(429)] [InlineData(500)]
    public async Task GenerateAsync_HttpFailure_DoesNotExposeProviderBody(int status)
    {
        using var http = new HttpClient(new Handler((_, _) => Task.FromResult(new HttpResponseMessage((HttpStatusCode)status)
            { Content = new StringContent("secret-test private provider diagnostic") })));
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => new ReportCommentaryClient(http).GenerateAsync(new(), Settings));
        Assert.Contains(status.ToString(), error.Message); Assert.DoesNotContain("secret-test", error.Message);
    }

    [Theory]
    [InlineData("not json")] [InlineData("{}")] [InlineData("{\"choices\":[]}")]
    [InlineData("{\"choices\":[{\"message\":{\"content\":\"\"}}]}")]
    public async Task GenerateAsync_MalformedResponse_HasFriendlyError(string response)
    {
        using var http = new HttpClient(new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(response) })));
        await Assert.ThrowsAsync<InvalidOperationException>(() => new ReportCommentaryClient(http).GenerateAsync(new(), Settings));
    }

    [Fact]
    public async Task GenerateAsync_Disabled_DoesNotMakeHttpRequest()
    {
        using var http = new HttpClient(new Handler((_, _) => throw new Xunit.Sdk.XunitException("No request expected")));
        await Assert.ThrowsAsync<InvalidOperationException>(() => new ReportCommentaryClient(http).GenerateAsync(new(), new()));
    }

    [Fact]
    public async Task GenerateAsync_NetworkFailure_HidesEndpointDetails()
    {
        using var http = new HttpClient(new Handler((_, _) => throw new HttpRequestException("secret-test endpoint")));
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => new ReportCommentaryClient(http).GenerateAsync(new(), Settings));
        Assert.DoesNotContain("secret-test", error.Message);
    }

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => send(request, token);
    }
    private sealed class ManualClock : TimeProvider
    {
        private TimerCallback? _callback;
        private object? _state;
        public TimeSpan DueTime { get; private set; }
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        { _callback = callback; _state = state; DueTime = dueTime; return new Timer(); }
        public void Fire() => _callback!(_state);
        private sealed class Timer : ITimer
        {
            public bool Change(TimeSpan dueTime, TimeSpan period) => true;
            public void Dispose() { }
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }
}
