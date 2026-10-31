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
    public async Task UploadAsync_WithExistingClone_FetchesWritesCommitsAndPushes()
    {
        var repoDir = EnsureExistingClone();
        _runner.Responder = arguments => arguments[0] switch
        {
            "fetch" or "checkout" or "add" or "commit" or "push" => new GitRunResult(0, string.Empty, string.Empty),
            "symbolic-ref" => new GitRunResult(0, "main" + Environment.NewLine, string.Empty),
            "rev-parse" => arguments.Contains("--quiet")
                ? new GitRunResult(0, "origin/main" + Environment.NewLine, string.Empty)
                : new GitRunResult(0, "abc1234" + Environment.NewLine, string.Empty),
            "status" => new GitRunResult(0, "M game-stats/README.md" + Environment.NewLine, string.Empty),
            "config" => new GitRunResult(0, "Mason" + Environment.NewLine, string.Empty),
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
            new[]
            {
                "remote",
                "remote",
                "fetch",
                "ls-remote",
                "ls-remote",
                "fetch",
                "rev-parse",
                "checkout",
                "add",
                "status",
                "config",
                "config",
                "commit",
                "push",
                "rev-parse"
            },
            _runner.Calls.Select(call => call.Arguments[0]).ToArray());

        var writtenReport = File.ReadAllText(Path.Combine(repoDir, "game-stats", "README.md"));
        Assert.Equal("# report", writtenReport);
        Assert.True(File.Exists(Path.Combine(repoDir, "game-stats", "daily.csv")));
    }

    [Fact]
    public async Task UploadAsync_ShallowCloneFetchesConfiguredNonDefaultBranchBeforeCheckout()
    {
        EnsureExistingClone();
        var targetFetched = false;
        _runner.Responder = arguments =>
        {
            if (arguments[0] == "fetch" && arguments.Contains("+refs/heads/archive:refs/remotes/origin/archive")) targetFetched = true;
            if (arguments[0] == "rev-parse" && arguments.Contains("--quiet"))
                return new(targetFetched ? 0 : 1, "", "");
            if (arguments[0] == "status") return new(0, "M game-stats/README.md", "");
            return new(0, "value", "");
        };
        await _channel.UploadAsync(Settings("archive"), SingleFile(), "msg");
        Assert.True(targetFetched);
        var checkout = _runner.Calls.Single(c => c.Arguments[0] == "checkout");
        Assert.Contains("origin/archive", checkout.Arguments);
    }

    [Fact]
    public async Task UploadAsync_WhenCloneMissing_ClonesFirst()
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
        Assert.Equal(
            new[] { "clone", "--depth", "1", "https://github.com/owner/repo.git", CloneName("owner/repo") },
            cloneCall.Arguments);
        Assert.DoesNotContain(_runner.Calls, call => call.Arguments[0] == "fetch");
    }

    [Fact]
    public async Task UploadAsync_WithConfiguredBranch_ChecksOutRemoteBranch()
    {
        EnsureExistingClone();
        _runner.Responder = arguments => arguments[0] switch
        {
            "fetch" or "ls-remote" => new GitRunResult(0, string.Empty, string.Empty),
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
    public async Task UploadAsync_WithNoChanges_SkipsCommitAndPush()
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
    public async Task UploadAsync_WhenPushFails_ThrowsWithStderrDetail()
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
    public async Task UploadAsync_WithDirectoryEscape_RejectsPath()
    {
        EnsureExistingClone();
        _runner.Responder = _ => new GitRunResult(0, string.Empty, string.Empty);
        var settings = Settings();
        settings.Directory = "../outside";

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => _channel.UploadAsync(settings, SingleFile(), "msg", CancellationToken.None));
    }

    [Fact]
    public async Task ValidateAsync_WhenPushDryRunFails_ThrowsActionableMessage()
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

    [Fact]
    public async Task UploadAsync_WhenDefaultBranchUpstreamAdvanced_AlignsBeforeCommit()
    {
        EnsureExistingClone();
        _runner.Responder = arguments => arguments[0] switch
        {
            "fetch" => new GitRunResult(0, string.Empty, string.Empty),
            "symbolic-ref" => new GitRunResult(0, "main" + Environment.NewLine, string.Empty),
            "rev-parse" => new GitRunResult(0, "origin/main" + Environment.NewLine, string.Empty),
            "checkout" => new GitRunResult(0, string.Empty, string.Empty),
            "add" => new GitRunResult(0, string.Empty, string.Empty),
            "status" => new GitRunResult(0, string.Empty, string.Empty),
            _ => new GitRunResult(1, string.Empty, "unexpected")
        };

        await _channel.UploadAsync(Settings(), SingleFile(), "msg", CancellationToken.None);

        var checkout = _runner.Calls.First(call => call.Arguments[0] == "checkout");
        Assert.Equal(new[] { "checkout", "-f", "-B", "main", "origin/main" }, checkout.Arguments);
    }

    [Fact]
    public async Task UploadAsync_WithUserFileInManagedDirectory_LeavesUserFilesUntouched()
    {
        var repoDir = EnsureExistingClone();
        var userFile = Path.Combine(repoDir, "game-stats", "user-notes.md");
        Directory.CreateDirectory(Path.GetDirectoryName(userFile)!);
        File.WriteAllText(userFile, "user content");

        _runner.Responder = arguments => arguments[0] switch
        {
            "fetch" or "add" => new GitRunResult(0, string.Empty, string.Empty),
            "status" => new GitRunResult(0, string.Empty, string.Empty),
            _ => new GitRunResult(1, string.Empty, "unexpected")
        };

        var result = await _channel.UploadAsync(Settings(), SingleFile(), "msg", CancellationToken.None);

        Assert.True(result.NoChanges);
        Assert.Equal("user content", File.ReadAllText(userFile));
        // add/status 只针对受管文件，避免把用户自放文件卷进提交。
        var addCall = _runner.Calls.First(call => call.Arguments[0] == "add");
        Assert.Equal(
            new[] { "add", "-A", "game-stats/README.md", "game-stats/daily.csv", "game-stats/raw/playtime.csv" },
            addCall.Arguments);
    }

    [Fact]
    public async Task UploadAsync_WhenRawCsvRemovedFromPayload_DeletesStaleManagedFile()
    {
        var repoDir = EnsureExistingClone();
        var staleRaw = Path.Combine(repoDir, "game-stats", "raw", "playtime.csv");
        Directory.CreateDirectory(Path.GetDirectoryName(staleRaw)!);
        File.WriteAllText(staleRaw, "game,start_time,end_time,duration_minutes");

        _runner.Responder = arguments => arguments[0] switch
        {
            "fetch" or "add" or "commit" or "push" => new GitRunResult(0, string.Empty, string.Empty),
            "status" => new GitRunResult(0, "D game-stats/raw/playtime.csv" + Environment.NewLine, string.Empty),
            "config" => new GitRunResult(0, "Mason" + Environment.NewLine, string.Empty),
            "rev-parse" => new GitRunResult(0, "abc1234" + Environment.NewLine, string.Empty),
            _ => new GitRunResult(1, string.Empty, "unexpected")
        };

        var result = await _channel.UploadAsync(Settings(), SingleFile(), "msg", CancellationToken.None);

        Assert.False(result.NoChanges);
        Assert.False(File.Exists(staleRaw));
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
        var repoDir = Path.Combine(_cloneRoot, CloneName("owner/repo"));
        Directory.CreateDirectory(Path.Combine(repoDir, ".git"));
        return repoDir;
    }

    private static string CloneName(string repo) => "repo-" + Convert.ToHexString(
        System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(repo.ToUpperInvariant())));

    [Fact]
    public async Task UploadAsync_DifferentRepositoriesCannotShareSanitizedCloneDirectory()
    {
        var first = Settings(); first.Repo = "a-b/c";
        var second = Settings(); second.Repo = "a/b-c";
        await _channel.UploadAsync(first, SingleFile(), "first");
        await _channel.UploadAsync(second, SingleFile(), "second");
        var clones = _runner.Calls.Where(c => c.Arguments[0] == "clone").ToArray();
        Assert.Equal(2, clones.Length);
        Assert.NotEqual(clones[0].Arguments[^1], clones[1].Arguments[^1]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UploadAsync_UnexpectedFetchOrPushRemoteFailsBeforeWriting(bool push)
    {
        EnsureExistingClone();
        _runner.ChangedPushOnly = push;
        _runner.RemoteUrl = "https://github.com/other/repository.git";
        await Assert.ThrowsAsync<InvalidOperationException>(() => _channel.UploadAsync(Settings(), SingleFile(), "msg"));
        Assert.DoesNotContain(_runner.Calls, c => c.Arguments[0] is "add" or "push" or "fetch");
    }

    [Fact]
    public async Task UploadAsync_AdditionalPushUrlIsRejectedBeforeAnyManagedFileWrite()
    {
        var repoDir = EnsureExistingClone();
        _runner.ChangedPushOnly = true;
        _runner.RemoteUrl = "https://github.com/owner/repo.git\nhttps://github.com/other/repository.git";
        await Assert.ThrowsAsync<InvalidOperationException>(() => _channel.UploadAsync(Settings(), SingleFile(), "msg"));
        Assert.False(Directory.Exists(Path.Combine(repoDir, "game-stats")));
        Assert.DoesNotContain(_runner.Calls, c => c.Arguments[0] is "add" or "push" or "fetch");
    }

    [Fact]
    public async Task UploadAsync_ClearingConfiguredBranchReturnsToRemoteDefaultNotLocalCheckout()
    {
        EnsureExistingClone();
        _runner.RemoteDefaultBranch = "main";
        _runner.Responder = arguments => new GitRunResult(0,
            arguments[0] == "symbolic-ref" ? "stats" : arguments[0] == "status" ? "M game-stats/README.md" : "value", "");
        var settings = Settings("stats");
        await _channel.UploadAsync(settings, SingleFile(), "stats");
        _runner.Calls.Clear();
        settings.Branch = null;
        await _channel.UploadAsync(settings, SingleFile(), "default");
        Assert.Contains("origin/main", _runner.Calls.Single(c => c.Arguments[0] == "checkout").Arguments);
    }

    [Fact]
    public async Task UploadAsync_RemoteDefaultChangeFetchesNewDefaultBeforeCheckout()
    {
        EnsureExistingClone();
        _runner.RemoteDefaultBranch = "new-default";
        var fetched = false;
        _runner.Responder = arguments =>
        {
            if (arguments[0] == "fetch" && arguments.Contains("+refs/heads/new-default:refs/remotes/origin/new-default")) fetched = true;
            if (arguments[0] == "rev-parse" && arguments.Contains("--quiet")) return new(fetched ? 0 : 1, "", "");
            return new(0, arguments[0] == "symbolic-ref" ? "main" : "", "");
        };
        await _channel.UploadAsync(Settings(), SingleFile(), "default changed");
        Assert.True(fetched);
        Assert.Contains("origin/new-default", _runner.Calls.Single(c => c.Arguments[0] == "checkout").Arguments);
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
        public string? RemoteUrl { get; set; }
        public bool ChangedPushOnly { get; set; }
        public string? RemoteDefaultBranch { get; set; }
        public List<(string? WorkingDirectory, IReadOnlyList<string> Arguments)> Calls { get; } = new();

        public Func<IReadOnlyList<string>, GitRunResult> Responder { get; set; } =
            _ => new GitRunResult(0, string.Empty, string.Empty);

        public Task<GitRunResult> RunAsync(
            string? workingDirectory,
            IReadOnlyList<string> arguments,
            CancellationToken cancellationToken = default)
        {
            Calls.Add((workingDirectory, arguments));
            if (arguments[0] == "ls-remote" && arguments.Contains("--symref"))
            {
                var local = Responder(["symbolic-ref", "--short", "HEAD"]);
                var branch = RemoteDefaultBranch ?? (local.Succeeded ? local.StandardOutput.Trim() : null);
                return Task.FromResult(new GitRunResult(0, string.IsNullOrEmpty(branch) ? "" : $"ref: refs/heads/{branch}\tHEAD\ncommit\tHEAD", ""));
            }
            if (arguments[0] == "ls-remote" && arguments.Contains("--heads"))
                return Task.FromResult(new GitRunResult(0, $"commit\t{arguments[^1]}", ""));
            if (arguments[0] == "remote")
            {
                var urls = RemoteUrl is not null && (!ChangedPushOnly || arguments.Contains("--push"))
                    ? RemoteUrl : Calls.LastOrDefault(c => c.Arguments[0] == "clone").Arguments?[3]
                        ?? "https://github.com/owner/repo.git";
                // Like git, get-url returns only the first URL unless --all is supplied.
                return Task.FromResult(new GitRunResult(0, arguments.Contains("--all") ? urls : urls.Split('\n')[0], ""));
            }
            return Task.FromResult(Responder(arguments));
        }
    }
}
