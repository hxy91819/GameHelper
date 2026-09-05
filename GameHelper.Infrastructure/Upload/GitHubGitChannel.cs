using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using GameHelper.Core.Abstractions;
using GameHelper.Core.Models;
using GameHelper.Core.Utilities;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace GameHelper.Infrastructure.Upload;

/// <summary>
/// 基于 git.exe 的 GitHub 上传渠道：在数据目录维护一个专属克隆工作副本，
/// 每次上传对齐远端后重写受管文件、按需提交并推送。
/// 凭据复用本机 git 凭据管理器，无需在配置中保存 token。
/// 只管理 <see cref="ManagedFileNames"/> 列出的文件；sync.directory 内用户自放文件不会被推送或删除。
/// </summary>
public sealed class GitHubGitChannel : IStatsUploadChannel
{
    private const string CommitterName = "GameHelper";
    private const string CommitterEmail = "gamehelper@users.noreply.github.com";

    /// <summary>GameHelper 在远端目录内托管的文件（相对 sync.directory）；其余文件不受推送影响。</summary>
    private static readonly string[] ManagedFileNames =
    [
        "README.md",
        "daily.csv",
        "raw/playtime.csv"
    ];

    private readonly IGitRunner _git;
    private readonly string _cloneRoot;
    private readonly ILogger<GitHubGitChannel> _logger;

    public GitHubGitChannel(
        IGitRunner? gitRunner = null,
        ILogger<GitHubGitChannel>? logger = null,
        string? cloneRoot = null)
    {
        _git = gitRunner ?? new ProcessGitRunner();
        _logger = logger ?? NullLogger<GitHubGitChannel>.Instance;
        _cloneRoot = cloneRoot ?? AppDataPath.GetSyncCloneRoot();
    }

    public async Task ValidateAsync(SyncSettings settings, CancellationToken cancellationToken = default)
    {
        var repoDir = await EnsureCloneAsync(settings, cancellationToken).ConfigureAwait(false);

        // push --dry-run 会真实走一遍远端鉴权但不产生提交，是凭据是否就绪的最可靠探针。
        var push = await _git
            .RunAsync(repoDir, new[] { "push", "--dry-run", "origin", "HEAD" }, cancellationToken)
            .ConfigureAwait(false);
        if (!push.Succeeded)
        {
            throw new InvalidOperationException(
                $"git 推送凭据校验失败：{push.DescribeError()}。"
                + "请确认本机 git 已配置该仓库的推送凭据（可手动执行一次推送建立凭据），或在 config.yml 改用 sync.method: api。");
        }
    }

    public async Task<StatsUploadResult> UploadAsync(
        SyncSettings settings,
        IReadOnlyList<StatsUploadFile> files,
        string commitMessage,
        CancellationToken cancellationToken = default)
    {
        var repoDir = await EnsureCloneAsync(settings, cancellationToken).ConfigureAwait(false);
        var targetDir = ResolveSafeTargetDirectory(repoDir, settings.NormalizedDirectory);

        WriteManagedFiles(targetDir, files);

        var add = await _git
            .RunAsync(repoDir, BuildPathspecArguments("add", "-A", settings.NormalizedDirectory), cancellationToken)
            .ConfigureAwait(false);
        EnsureSucceeded(add, "git add");

        var status = await _git
            .RunAsync(repoDir, BuildPathspecArguments("status", "--porcelain", settings.NormalizedDirectory), cancellationToken)
            .ConfigureAwait(false);
        EnsureSucceeded(status, "git status");
        if (string.IsNullOrWhiteSpace(status.StandardOutput))
        {
            _logger.LogDebug("推送目录 {Directory} 无变化，跳过提交", settings.NormalizedDirectory);
            return new StatsUploadResult(CommitId: null, NoChanges: true);
        }

        var commitArguments = new List<string>();
        if (!await HasUserIdentityAsync(repoDir, cancellationToken).ConfigureAwait(false))
        {
            commitArguments.AddRange(new[] { "-c", $"user.name={CommitterName}", "-c", $"user.email={CommitterEmail}" });
        }

        commitArguments.AddRange(new[] { "commit", "-m", commitMessage });
        var commit = await _git.RunAsync(repoDir, commitArguments, cancellationToken).ConfigureAwait(false);
        EnsureSucceeded(commit, "git commit");

        var push = await _git
            .RunAsync(repoDir, new[] { "push", "origin", "HEAD" }, cancellationToken)
            .ConfigureAwait(false);
        EnsureSucceeded(push, "git push");

        var revision = await _git
            .RunAsync(repoDir, new[] { "rev-parse", "--short", "HEAD" }, cancellationToken)
            .ConfigureAwait(false);
        var commitId = revision.Succeeded ? revision.StandardOutput.Trim() : null;
        _logger.LogInformation("git 渠道推送完成：{CommitId}", commitId ?? "(unknown)");
        return new StatsUploadResult(commitId, NoChanges: false);
    }

    /// <summary>确保工作副本存在、与远端对齐，返回仓库工作目录。</summary>
    private async Task<string> EnsureCloneAsync(SyncSettings settings, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(_cloneRoot);
        var dirName = SanitizeRepoName(settings.NormalizedRepo);
        var repoDir = Path.Combine(_cloneRoot, dirName);

        if (!Directory.Exists(Path.Combine(repoDir, ".git")))
        {
            // 目录存在但没有 .git：上次 clone 中断留下的残骸，清掉重来。
            if (Directory.Exists(repoDir))
            {
                try
                {
                    Directory.Delete(repoDir, recursive: true);
                }
                catch (Exception ex)
                {
                    throw new InvalidOperationException($"清理不完整的工作副本失败：{repoDir}", ex);
                }
            }

            var clone = await _git
                .RunAsync(
                    _cloneRoot,
                    new[] { "clone", "--depth", "1", CloneUrl(settings.NormalizedRepo), dirName },
                    cancellationToken)
                .ConfigureAwait(false);
            if (!clone.Succeeded)
            {
                throw new InvalidOperationException(
                    $"git clone 失败：{clone.DescribeError()}。请确认仓库 {settings.NormalizedRepo} 存在且本机 git 有访问权限。");
            }
        }
        else
        {
            var fetch = await _git
                .RunAsync(repoDir, new[] { "fetch", "origin", "--prune" }, cancellationToken)
                .ConfigureAwait(false);
            if (!fetch.Succeeded)
            {
                throw new InvalidOperationException(
                    $"git fetch 失败：{fetch.DescribeError()}。请检查网络与凭据；也可删除目录 {repoDir} 后重试。");
            }
        }

        // 显式指定分支或使用克隆当前分支，都强制对齐到对应远端分支，
        // 避免远端被他人推进后本地提交因非快进被拒。
        var branch = settings.NormalizedBranch ?? await ResolveCurrentBranchAsync(repoDir, cancellationToken).ConfigureAwait(false);
        if (branch is not null)
        {
            await CheckoutBranchAsync(repoDir, branch, cancellationToken).ConfigureAwait(false);
        }

        return repoDir;
    }

    private async Task<string?> ResolveCurrentBranchAsync(string repoDir, CancellationToken cancellationToken)
    {
        var current = await _git
            .RunAsync(repoDir, new[] { "symbolic-ref", "--short", "HEAD" }, cancellationToken)
            .ConfigureAwait(false);
        if (!current.Succeeded)
        {
            return null;
        }

        var branch = current.StandardOutput.Trim();
        return branch.Length == 0 ? null : branch;
    }

    private async Task CheckoutBranchAsync(string repoDir, string branch, CancellationToken cancellationToken)
    {
        var remoteBranch = $"refs/remotes/origin/{branch}";
        var verify = await _git
            .RunAsync(repoDir, new[] { "rev-parse", "--verify", "--quiet", remoteBranch }, cancellationToken)
            .ConfigureAwait(false);

        IReadOnlyList<string> arguments = verify.Succeeded
            ? new[] { "checkout", "-f", "-B", branch, $"origin/{branch}" }
            : new[] { "checkout", "-B", branch };

        var checkout = await _git.RunAsync(repoDir, arguments, cancellationToken).ConfigureAwait(false);
        if (!checkout.Succeeded)
        {
            throw new InvalidOperationException($"git checkout {branch} 失败：{checkout.DescribeError()}");
        }
    }

    private static IReadOnlyList<string> BuildPathspecArguments(
        string command,
        string option,
        string normalizedDirectory)
    {
        var arguments = new List<string> { command, option };
        arguments.AddRange(ManagedFileNames.Select(file => $"{normalizedDirectory}/{file}"));
        return arguments;
    }

    /// <summary>重写受管文件：仅删除 GameHelper 托管且本次不再上传的文件，不动目录内用户自放文件。</summary>
    private static void WriteManagedFiles(string targetDirectory, IReadOnlyList<StatsUploadFile> files)
    {
        var uploaded = new HashSet<string>(files.Select(file => file.RelativePath.Replace('\\', '/')), StringComparer.OrdinalIgnoreCase);
        foreach (var managed in ManagedFileNames)
        {
            if (uploaded.Contains(managed))
            {
                continue;
            }

            var stalePath = Path.Combine(targetDirectory, managed.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(stalePath))
            {
                File.Delete(stalePath);
            }
        }

        Directory.CreateDirectory(targetDirectory);

        foreach (var file in files)
        {
            var relative = file.RelativePath.Replace('\\', '/');
            if (!ManagedFileNames.Contains(relative, StringComparer.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException($"试图推送未托管文件：{relative}");
            }

            var fullPath = Path.GetFullPath(
                Path.Combine(targetDirectory, relative.Replace('/', Path.DirectorySeparatorChar)));
            if (!fullPath.StartsWith(targetDirectory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException($"非法的推送路径：{file.RelativePath}");
            }

            var directory = Path.GetDirectoryName(fullPath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            File.WriteAllText(fullPath, file.Content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        }
    }

    private static string ResolveSafeTargetDirectory(string repoDir, string normalizedDirectory)
    {
        var fullRepo = Path.GetFullPath(repoDir);
        var target = Path.GetFullPath(
            Path.Combine(fullRepo, normalizedDirectory.Replace('/', Path.DirectorySeparatorChar)));
        if (!target.StartsWith(fullRepo + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"非法的 sync.directory：{normalizedDirectory}");
        }

        return target;
    }

    private async Task<bool> HasUserIdentityAsync(string repoDir, CancellationToken cancellationToken)
    {
        var name = await _git.RunAsync(repoDir, new[] { "config", "user.name" }, cancellationToken).ConfigureAwait(false);
        var email = await _git.RunAsync(repoDir, new[] { "config", "user.email" }, cancellationToken).ConfigureAwait(false);
        return name.Succeeded && !string.IsNullOrWhiteSpace(name.StandardOutput)
            && email.Succeeded && !string.IsNullOrWhiteSpace(email.StandardOutput);
    }

    private static void EnsureSucceeded(GitRunResult result, string operation)
    {
        if (!result.Succeeded)
        {
            throw new InvalidOperationException($"{operation} 失败：{result.DescribeError()}");
        }
    }

    private static string CloneUrl(string repo) => $"https://github.com/{repo}.git";

    private static string SanitizeRepoName(string repo)
    {
        var sanitized = repo.Replace('/', '-');
        var builder = new StringBuilder(sanitized.Length);
        foreach (var c in sanitized)
        {
            builder.Append(char.IsLetterOrDigit(c) || c is '-' or '.' or '_' ? c : '_');
        }

        return builder.ToString();
    }
}
