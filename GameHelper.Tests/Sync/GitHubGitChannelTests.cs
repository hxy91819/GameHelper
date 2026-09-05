using GameHelper.Core.Abstractions;
using GameHelper.Core.Models;
using GameHelper.Infrastructure.Upload;
using Microsoft.Extensions.Logging.Abstractions;

namespace GameHelper.Tests.Sync;

public class GitHubGitChannelTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _cloneRoot;
    private readonly FakeGitRunner _runner = new();
    private readonly GitHubGitChannel _channel;

    public GitHubGitChannelTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "GameHelperTests_GitChannel", Guid.NewGuid().ToString("N"));
        _cloneRoot = Path.Combine(_tempDir, "clone-root");
        Directory.CreateDirectory(_cloneRoot);
        _channel = new GitHubGitChannel(
            _runner,
            NullLogger<GitHubGitChannel>.Instance,
            _cloneRoot);
    }

    [Fact]
    public async Task Upload_WithExistingClone_FetchesWritesCommitsAndPushes()
    {
        var repoDir = EnsureExistingClone();
        _runner.Responder = arguments => arguments[0] switch
        {
            "fetch" or "add" or "commit" or "push" => new GitRunResult(0, string.Empty, string.Empty),
            "status" => new GitRunResult(0, "M game-stats/README.md" + Environment.NewLine, string.Empty),
            "config" => new GitRunResult(0, "Mason" + Environment.NewLine, string.Empty),
            "rev-parse" => new GitRunResult(0, "abc1234" + Environment.NewLine, string.Empty),
            _ => new GitRunResult(1, string.Empty, "unexpected")
        };
        var files = new List<StatsUploadFile>
        {
            new("README.md", "# report"),
            new("daily.csv", "date,game,minutes\n")
        };

        var result = await _channel.UploadAsync(Settings(), files, "Update game stats", CancellationToken.None);

        Assert.Equal("abc1234", result.CommitId);
        Assert.False(result.NoChanges);

        Assert.Equal(
            new[] { "fetch", "add", "status", "config", "config", "commit", "push", "rev-parse" },
            _runner.Calls.Select(call => call.Arguments[0]).ToArray());

        var writtenReport = File.ReadAllText(Path.Combine(repoDir, "game-stats", "README.md"));
        Assert.Equal("# report", writtenReport);
        Assert.True(File.Exists(Path.Combine(repoDir, "game-stats", "daily.csv")));
    }

    [Fact]
    public async Task Upload_WhenCloneMissing_ClonesFirst()
    {
        _runner.Responder = arguments => arguments[0] switch
        {
            "clone" or "add" or "commit" or "push" => new GitRunResult(0, string.Empty, string.Empty),
            "status" => new GitRunResult(0, "A game-stats/README.md" + Environment.NewLine, string.Empty),
            "config" => new GitRunResult(0, "Mason" + Environment.NewLine, string.Empty),
            "rev-parse" => new GitRunResult(0, "abc1234" + Environment.NewLine, string.Empty),
            _ => new GitRunResult(1, string.Empty, "unexpected")
        };

        await _channel.UploadAsync(Settings(), SingleFile(), "msg", CancellationToken.None);

        var cloneCall = _runner.Calls.First(call => call.Arguments[0] == "clone");
        Assert.Equal("https://github.com/owner/repo.git", cloneCall.Arguments[1]);
        Assert.Equal("owner-repo", cloneCall.Arguments[2]);
        Assert.DoesNotContain(_runner.Calls, call => call.Arguments[0] == "fetch");
    }

    [Fact]
    public async Task Upload_WithConfiguredBranch_ChecksOutRemoteBranch()
    {
        EnsureExistingClone();
        _runner.Responder = arguments => arguments[0] switch
        {
            "fetch" => new GitRunResult(0, string.Empty, string.Empty),
            "rev-parse" => new GitRunResult(0, "origin/main" + Environment.NewLine, string.Empty),
            "checkout" => new GitRunResult(0, string.Empty, string.Empty),
            "add" => new GitRunResult(0, string.Empty, string.Empty),
            "status" => new GitRunResult(0, string.Empty, string.Empty),
            _ => new GitRunResult(1, string.Empty, "unexpected")
        };

        var result = await _channel.UploadAsync(Settings(branch: "main"), SingleFile(), "msg", CancellationToken.None);

        Assert.True(result.NoChanges);
        var checkout = _runner.Calls.First(call => call.Arguments[0] == "checkout");
        Assert.Equal(new[] { "checkout", "-f", "-B", "main", "origin/main" }, checkout.Arguments);
    }

    [Fact]
    public async Task Upload_WithNoChanges_SkipsCommitAndPush()
    {
        EnsureExistingClone();
        _runner.Responder = arguments => arguments[0] switch
        {
            "fetch" or "add" => new GitRunResult(0, string.Empty, string.Empty),
            "status" => new GitRunResult(0, string.Empty, string.Empty),
            _ => new GitRunResult(1, string.Empty, "unexpected")
        };

        var result = await _channel.UploadAsync(Settings(), SingleFile(), "msg", CancellationToken.None);

        Assert.True(result.NoChanges);
        Assert.DoesNotContain(_runner.Calls, call => call.Arguments[0] == "commit");
        Assert.DoesNotContain(_runner.Calls, call => call.Arguments[0] == "push");
    }

    [Fact]
    public async Task Upload_WhenPushFails_ThrowsWithStderrDetail()
    {
        EnsureExistingClone();
        _runner.Responder = arguments => arguments[0] switch
        {
            "fetch" or "add" => new GitRunResult(0, string.Empty, string.Empty),
            "status" => new GitRunResult(0, "M game-stats/README.md" + Environment.NewLine, string.Empty),
            "config" => new GitRunResult(0, "Mason" + Environment.NewLine, string.Empty),
            "commit" => new GitRunResult(0, string.Empty, string.Empty),
            "push" => new GitRunResult(128, string.Empty, "error: failed to push some refs"),
            _ => new GitRunResult(1, string.Empty, "unexpected")
        };

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => _channel.UploadAsync(Settings(), SingleFile(), "msg", CancellationToken.None));

        Assert.Contains("git push", ex.Message);
        Assert.Contains("failed to push", ex.Message);
    }

    [Fact]
    public async Task Upload_WithDirectoryEscape_RejectsPath()
    {
        EnsureExistingClone();
        _runner.Responder = _ => new GitRunResult(0, string.Empty, string.Empty);
        var settings = Settings();
        settings.Directory = "../outside";

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => _channel.UploadAsync(settings, SingleFile(), "msg", CancellationToken.None));
    }

    [Fact]
    public async Task Validate_WhenPushDryRunFails_ThrowsActionableMessage()
    {
        EnsureExistingClone();
        _runner.Responder = arguments => arguments[0] switch
        {
            "fetch" => new GitRunResult(0, string.Empty, string.Empty),
            "push" => new GitRunResult(128, string.Empty, "fatal: Authentication failed"),
            _ => new GitRunResult(1, string.Empty, "unexpected")
        };

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => _channel.ValidateAsync(Settings(), CancellationToken.None));

        Assert.Contains("sync.method: api", ex.Message);
        Assert.Contains("Authentication failed", ex.Message);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDir))
            {
                Directory.Delete(_tempDir, recursive: true);
            }
        }
        catch
        {
            // best-effort cleanup
        }
    }

    private string EnsureExistingClone()
    {
        var repoDir = Path.Combine(_cloneRoot, "owner-repo");
        Directory.CreateDirectory(Path.Combine(repoDir, ".git"));
        return repoDir;
    }

    private static SyncSettings Settings(string? branch = null) => new()
    {
        Enabled = true,
        Method = "git",
        Repo = "owner/repo",
        Branch = branch,
        Directory = "game-stats"
    };

    private static IReadOnlyList<StatsUploadFile> SingleFile() => new List<StatsUploadFile> { new("README.md", "# report") };

    private sealed class FakeGitRunner : IGitRunner
    {
        public List<(string? WorkingDirectory, IReadOnlyList<string> Arguments)> Calls { get; } = new();

        public Func<IReadOnlyList<string>, GitRunResult> Responder { get; set; } =
            _ => new GitRunResult(0, string.Empty, string.Empty);

        public Task<GitRunResult> RunAsync(
            string? workingDirectory,
            IReadOnlyList<string> arguments,
            CancellationToken cancellationToken = default)
        {
            Calls.Add((workingDirectory, arguments));
            return Task.FromResult(Responder(arguments));
        }
    }
}
