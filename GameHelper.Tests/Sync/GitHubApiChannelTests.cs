using System.Net;
using System.Text;
using GameHelper.Core.Abstractions;
using GameHelper.Core.Models;
using GameHelper.Infrastructure.Upload;
using Microsoft.Extensions.Logging.Abstractions;

namespace GameHelper.Tests.Sync;

public class GitHubApiChannelTests
{
    private const string RepoJson = """{"default_branch":"main"}""";
    private const string RefJson = """{"object":{"sha":"REFSHA"}}""";
    private const string CommitJson = """{"tree":{"sha":"BASETREE"}}""";
    private const string NewCommitJson = """{"sha":"NEWSHA1234567890"}""";

    [Fact]
    public async Task UploadAsync_BuildsSingleAtomicCommit()
    {
        var handler = new FakeGitHubHandler(ComposeDefaultResponder());
        var channel = CreateChannel(handler);
        var settings = ApiSettings();
        var files = new List<StatsUploadFile>
        {
            new("README.md", "# report"),
            new("daily.csv", "date,game,minutes\n")
        };

        var result = await channel.UploadAsync(settings, files, "Update game stats", CancellationToken.None);

        Assert.Equal("NEWSHA1234", result.CommitId);
        Assert.False(result.NoChanges);

        var paths = handler.Requests.Select(request => $"{request.Method.Method} {request.RequestUri!.AbsolutePath}").ToList();
        Assert.Equal(
        [
            "GET /repos/o/r",
            "GET /repos/o/r/git/ref/heads/main",
            "GET /repos/o/r/git/commits/REFSHA",
            "POST /repos/o/r/git/blobs",
            "POST /repos/o/r/git/blobs",
            "GET /repos/o/r/git/trees/BASETREE",
            "POST /repos/o/r/git/trees",
            "POST /repos/o/r/git/commits",
            "PATCH /repos/o/r/git/refs/heads/main"
        ], paths);

        // 鉴权与 UA
        Assert.All(handler.Requests, request =>
        {
            Assert.Equal("Bearer", request.Headers.Authorization!.Scheme);
            Assert.Equal("tok", request.Headers.Authorization.Parameter);
            Assert.True(request.Headers.UserAgent.ToString().Contains("GameHelper"));
        });

        // blob 内容为 base64，tree 路径带目录前缀
        var blobBody = handler.Bodies.First(body => body.Contains("\"encoding\""));
        Assert.Contains(Convert.ToBase64String(Encoding.UTF8.GetBytes("# report")), blobBody);
        var treeBody = handler.Bodies.First(body => body.Contains("base_tree"));
        Assert.Contains("game-stats/README.md", treeBody);
        Assert.Contains("BASETREE", treeBody);
        // 倒数第二个请求体是 create commit（携带 parent），最后一个才是 PATCH ref。
        Assert.Contains("REFSHA", handler.Bodies[^2]);
    }

    [Fact]
    public async Task UploadAsync_WhenRepositoryMissing_ThrowsActionableError()
    {
        var handler = new FakeGitHubHandler((request, _) =>
            request.RequestUri!.AbsolutePath == "/repos/o/r"
                ? JsonResponse(HttpStatusCode.NotFound, "{}")
                : JsonResponse(HttpStatusCode.OK, RefJson));
        var channel = CreateChannel(handler);

        var ex = await Assert.ThrowsAsync<GitHubApiException>(
            () => channel.UploadAsync(ApiSettings(), SingleFile(), "msg", CancellationToken.None));

        Assert.Equal(404, ex.StatusCode);
        Assert.Contains("不存在", ex.Message);
    }

    [Fact]
    public async Task UploadAsync_WhenReferenceConflicts_RetriesFromScratch()
    {
        var patchCalls = 0;
        var handler = new FakeGitHubHandler((request, _) =>
        {
            if (request.Method == HttpMethod.Patch)
            {
                patchCalls++;
                return patchCalls == 1
                    ? JsonResponse(HttpStatusCode.Conflict, "{}")
                    : JsonResponse(HttpStatusCode.OK, "{}");
            }

            return ComposeDefaultResponder()(request, _);
        });
        var channel = CreateChannel(handler);

        var result = await channel.UploadAsync(ApiSettings(), SingleFile(), "msg", CancellationToken.None);

        Assert.Equal("NEWSHA1234", result.CommitId);
        Assert.Equal(2, patchCalls);
    }

    [Fact]
    public async Task UploadAsync_WithoutConfiguredToken_ThrowsActionableError()
    {
        var previous = Environment.GetEnvironmentVariable(SyncSettings.TokenEnvironmentVariable);
        Environment.SetEnvironmentVariable(SyncSettings.TokenEnvironmentVariable, null);
        try
        {
            var channel = CreateChannel(new FakeGitHubHandler(ComposeDefaultResponder()));

            var ex = await Assert.ThrowsAsync<InvalidOperationException>(
                () => channel.UploadAsync(
                    ApiSettings(token: null),
                    SingleFile(),
                    "msg",
                    CancellationToken.None));

            Assert.Contains(SyncSettings.TokenEnvironmentVariable, ex.Message);
        }
        finally
        {
            Environment.SetEnvironmentVariable(SyncSettings.TokenEnvironmentVariable, previous);
        }
    }

    [Fact]
    public async Task UploadAsync_UsesTokenFromEnvironmentWhenConfigEmpty()
    {
        var previous = Environment.GetEnvironmentVariable(SyncSettings.TokenEnvironmentVariable);
        Environment.SetEnvironmentVariable(SyncSettings.TokenEnvironmentVariable, "env-token");
        try
        {
            string? seenAuthorization = null;
            var handler = new FakeGitHubHandler((request, body) =>
            {
                seenAuthorization ??= request.Headers.Authorization?.Parameter;
                return ComposeDefaultResponder()(request, body);
            });
            var channel = CreateChannel(handler);

            await channel.UploadAsync(
                ApiSettings(token: null),
                SingleFile(),
                "msg",
                CancellationToken.None);

            Assert.Equal("env-token", seenAuthorization);
        }
        finally
        {
            Environment.SetEnvironmentVariable(SyncSettings.TokenEnvironmentVariable, previous);
        }
    }

    [Fact]
    public async Task UploadAsync_DeletesStaleManagedFilesNotInPayload()
    {
        // 基础树里包含上一轮推送过的 raw/playtime.csv；本次 payload 未包含它，
        // 必须生成 sha=null 的删除条目，否则含精确时间戳的文件残留在远端。
        var handler = new FakeGitHubHandler((request, body) =>
        {
            if (request.Method == HttpMethod.Get
                && request.RequestUri!.AbsolutePath.EndsWith("/git/trees/BASETREE", StringComparison.Ordinal))
            {
                return JsonResponse(HttpStatusCode.OK, """
                    {"tree":[
                        {"path":"game-stats/README.md","type":"blob"},
                        {"path":"game-stats/raw/playtime.csv","type":"blob"},
                        {"path":"outside/keep.txt","type":"blob"}
                    ]}
                    """);
            }

            return ComposeDefaultResponder()(request, body);
        });
        var channel = CreateChannel(handler);

        await channel.UploadAsync(ApiSettings(), SingleFile(), "msg", CancellationToken.None);

        var treeBody = handler.Bodies.First(body => body.Contains("base_tree"));
        Assert.Contains("game-stats/raw/playtime.csv", treeBody);
        Assert.Contains("\"sha\":null", treeBody);
        // 目录外的文件不受影响。
        Assert.DoesNotContain("outside/keep.txt", treeBody);
    }

    [Fact]
    public async Task ValidateAsync_WithAccessibleRepository_ChecksRepoAndBranch()
    {
        var handler = new FakeGitHubHandler(ComposeDefaultResponder());
        var channel = CreateChannel(handler);
        var settings = ApiSettings(branch: "main");

        await channel.ValidateAsync(settings, CancellationToken.None);

        var paths = handler.Requests.Select(request => request.RequestUri!.AbsolutePath).ToList();
        Assert.Equal(["/repos/o/r", "/repos/o/r/git/ref/heads/main"], paths);
    }

    private static Func<HttpRequestMessage, string, HttpResponseMessage> ComposeDefaultResponder()
    {
        var blobCounter = 0;
        return (request, _) =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (request.Method == HttpMethod.Get && path == "/repos/o/r")
            {
                return JsonResponse(HttpStatusCode.OK, RepoJson);
            }

            if (request.Method == HttpMethod.Get && path.EndsWith("/git/ref/heads/main", StringComparison.Ordinal))
            {
                return JsonResponse(HttpStatusCode.OK, RefJson);
            }

            if (request.Method == HttpMethod.Get && path.EndsWith("/git/commits/REFSHA", StringComparison.Ordinal))
            {
                return JsonResponse(HttpStatusCode.OK, CommitJson);
            }

            if (request.Method == HttpMethod.Get && path.EndsWith("/git/trees/BASETREE", StringComparison.Ordinal))
            {
                return JsonResponse(HttpStatusCode.OK, """{"tree":[]}""");
            }

            if (request.Method == HttpMethod.Post && path.EndsWith("/git/blobs", StringComparison.Ordinal))
            {
                blobCounter++;
                return JsonResponse(HttpStatusCode.OK, $"{{\"sha\":\"BLOB{blobCounter}\"}}");
            }

            if (request.Method == HttpMethod.Post && path.EndsWith("/git/trees", StringComparison.Ordinal))
            {
                return JsonResponse(HttpStatusCode.OK, """{"sha":"NEWTREE"}""");
            }

            if (request.Method == HttpMethod.Post && path.EndsWith("/git/commits", StringComparison.Ordinal))
            {
                return JsonResponse(HttpStatusCode.OK, NewCommitJson);
            }

            if (request.Method == HttpMethod.Patch && path.EndsWith("/git/refs/heads/main", StringComparison.Ordinal))
            {
                return JsonResponse(HttpStatusCode.OK, "{}");
            }

            return JsonResponse(HttpStatusCode.InternalServerError, "{}");
        };
    }

    private static GitHubApiChannel CreateChannel(FakeGitHubHandler handler)
    {
        var client = new HttpClient(handler) { BaseAddress = new Uri("https://unit.test/") };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("GameHelper");
        return new GitHubApiChannel(NullLogger<GitHubApiChannel>.Instance, client);
    }

    private static SyncSettings ApiSettings(string? token = "tok", string? branch = null) => new()
    {
        Enabled = true,
        Method = "api",
        Repo = "o/r",
        Token = token,
        Branch = branch,
        Directory = "game-stats"
    };

    private static IReadOnlyList<StatsUploadFile> SingleFile() => new List<StatsUploadFile> { new("README.md", "# report") };

    private static HttpResponseMessage JsonResponse(HttpStatusCode statusCode, string json) =>
        new(statusCode) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private sealed class FakeGitHubHandler(
        Func<HttpRequestMessage, string, HttpResponseMessage> responder) : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = new();

        public List<string> Bodies { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var body = request.Content is null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken);
            Requests.Add(request);
            Bodies.Add(body);
            return responder(request, body);
        }
    }
}
