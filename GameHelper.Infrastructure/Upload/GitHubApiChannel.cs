using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using GameHelper.Core.Abstractions;
using GameHelper.Core.Models;
using GameHelper.Core.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace GameHelper.Infrastructure.Upload;

/// <summary>
/// 基于 GitHub REST API（Git Data API）的上传渠道：把一批文件以单个原子提交写入目标分支。
/// 适合未安装 git 或需要无人值守稳定凭据的场景；token 来自 config.yml 或环境变量
/// <see cref="SyncSettings.TokenEnvironmentVariable"/>，绝不写入日志。
/// </summary>
public sealed class GitHubApiChannel : IStatsUploadChannel
{
    /// <summary>GitHub API 根地址。</summary>
    public const string ApiBase = "https://api.github.com";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _httpClient;
    private readonly ILogger<GitHubApiChannel> _logger;

    public GitHubApiChannel(ILogger<GitHubApiChannel>? logger = null, HttpClient? httpClient = null)
    {
        _logger = logger ?? NullLogger<GitHubApiChannel>.Instance;
        _httpClient = httpClient ?? CreateDefaultClient();
    }

    public async Task ValidateAsync(SyncSettings settings, CancellationToken cancellationToken = default)
    {
        var token = ResolveToken(settings);
        var (owner, name) = SplitRepo(settings);

        // GET 仓库本身：校验 token 有效性与仓库可见性。
        await SendAsync(HttpMethod.Get, $"/repos/{owner}/{name}", null, token, cancellationToken).ConfigureAwait(false);

        if (settings.NormalizedBranch is { } branch)
        {
            await GetBranchHeadAsync(owner, name, branch, token, cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task<StatsUploadResult> UploadAsync(
        SyncSettings settings,
        IReadOnlyList<StatsUploadFile> files,
        string commitMessage,
        CancellationToken cancellationToken = default)
    {
        var token = ResolveToken(settings);
        var (owner, name) = SplitRepo(settings);
        var branch = settings.NormalizedBranch
            ?? await GetDefaultBranchAsync(owner, name, token, cancellationToken).ConfigureAwait(false);

        // 远端引用可能在我们构建提交期间前进（409/422）；整段重来一次即可。
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                var commitId = await UploadOnceAsync(
                    owner,
                    name,
                    branch,
                    token,
                    settings,
                    files,
                    commitMessage,
                    cancellationToken).ConfigureAwait(false);
                return new StatsUploadResult(commitId, NoChanges: false);
            }
            catch (GitHubApiException ex) when (ex.StatusCode is 409 or 422 && attempt < 3)
            {
                _logger.LogWarning(ex, "GitHub 引用冲突（{Status}），重试第 {Attempt} 次", ex.StatusCode, attempt);
            }
        }
    }

    private async Task<string> UploadOnceAsync(
        string owner,
        string name,
        string branch,
        string token,
        SyncSettings settings,
        IReadOnlyList<StatsUploadFile> files,
        string commitMessage,
        CancellationToken cancellationToken)
    {
        var baseRefSha = await GetBranchHeadAsync(owner, name, branch, token, cancellationToken).ConfigureAwait(false);
        var baseCommit = await SendAsync(
            HttpMethod.Get,
            $"/repos/{owner}/{name}/git/commits/{baseRefSha}",
            null,
            token,
            cancellationToken).ConfigureAwait(false);
        var baseTreeSha = GetString(baseCommit, "tree", "sha");

        var prefix = $"{settings.NormalizedDirectory}/";
        var uploadedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var treeEntries = new List<object>();
        foreach (var file in files)
        {
            var path = prefix + file.RelativePath.Replace('\\', '/');
            uploadedPaths.Add(path);
            var contentBytes = Encoding.UTF8.GetBytes(file.Content);
            var blob = await SendAsync(
                HttpMethod.Post,
                $"/repos/{owner}/{name}/git/blobs",
                new { content = Convert.ToBase64String(contentBytes), encoding = "base64" },
                token,
                cancellationToken).ConfigureAwait(false);
            treeEntries.Add(new
            {
                path,
                mode = "100644",
                type = "blob",
                sha = GetString(blob, "sha")
            });
        }

        // base_tree 只增不删：目标目录内先前推送过、本次不再上传的托管文件（例如关闭
        // includeRawCsv 后的 raw/playtime.csv）必须显式生成删除条目，否则含精确时间戳的
        // 隐私文件会永远残留在远端。只清理托管清单内的文件，用户自放文件不动。
        foreach (var stalePath in await ListStaleManagedPathsAsync(
                owner,
                name,
                baseTreeSha,
                prefix,
                uploadedPaths,
                token,
                cancellationToken).ConfigureAwait(false))
        {
            treeEntries.Add(new { path = stalePath, mode = "100644", type = "blob", sha = (string?)null });
        }

        var tree = await SendAsync(
            HttpMethod.Post,
            $"/repos/{owner}/{name}/git/trees",
            new { base_tree = baseTreeSha, tree = treeEntries },
            token,
            cancellationToken).ConfigureAwait(false);
        var treeSha = GetString(tree, "sha");

        var commit = await SendAsync(
            HttpMethod.Post,
            $"/repos/{owner}/{name}/git/commits",
            new { message = commitMessage, tree = treeSha, parents = new[] { baseRefSha } },
            token,
            cancellationToken).ConfigureAwait(false);
        var commitSha = GetString(commit, "sha");

        await SendAsync(
            HttpMethod.Patch,
            $"/repos/{owner}/{name}/git/refs/heads/{branch}",
            new { sha = commitSha, force = false },
            token,
            cancellationToken).ConfigureAwait(false);

        _logger.LogInformation("GitHub API 渠道推送完成：{CommitSha}", commitSha);
        return commitSha.Length > 10 ? commitSha[..10] : commitSha;
    }

    /// <summary>
    /// 列出基础树中位于推送目录内、属于托管清单、且本次未再上传的 blob 路径（需要删除的残留文件）。
    /// </summary>
    private async Task<IReadOnlyList<string>> ListStaleManagedPathsAsync(
        string owner,
        string name,
        string baseTreeSha,
        string prefix,
        HashSet<string> uploadedPaths,
        string token,
        CancellationToken cancellationToken)
    {
        var stale = new List<string>();
        var baseTree = await SendAsync(
            HttpMethod.Get,
            $"/repos/{owner}/{name}/git/trees/{baseTreeSha}?recursive=1",
            null,
            token,
            cancellationToken).ConfigureAwait(false);

        if (baseTree.ValueKind != JsonValueKind.Object || !baseTree.TryGetProperty("tree", out var entries))
        {
            return stale;
        }

        foreach (var entry in entries.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.Object
                || !entry.TryGetProperty("path", out var pathElement)
                || !entry.TryGetProperty("type", out var typeElement))
            {
                continue;
            }

            var path = pathElement.GetString();
            if (path is null
                || !path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                || uploadedPaths.Contains(path)
                || !string.Equals(typeElement.GetString(), "blob", StringComparison.Ordinal))
            {
                continue;
            }

            var relativePath = path[prefix.Length..].Replace('\\', '/');
            if (!StatsReportBuilder.ManagedFileNames.Contains(relativePath, StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            stale.Add(path);
        }

        return stale;
    }

    public static string ResolveToken(SyncSettings settings)
    {
        if (!string.IsNullOrWhiteSpace(settings.Token))
        {
            return settings.Token.Trim();
        }

        var fromEnvironment = Environment.GetEnvironmentVariable(SyncSettings.TokenEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(fromEnvironment))
        {
            return fromEnvironment.Trim();
        }

        throw new InvalidOperationException(
            "缺少 GitHub token：请在 config.yml 的 sync.token 中填入 fine-grained PAT"
            + "（需授予目标仓库 Contents: Read and write），或设置环境变量 "
            + SyncSettings.TokenEnvironmentVariable + "。");
    }

    private async Task<string> GetBranchHeadAsync(
        string owner,
        string name,
        string branch,
        string token,
        CancellationToken cancellationToken)
    {
        try
        {
            var reference = await SendAsync(
                HttpMethod.Get,
                $"/repos/{owner}/{name}/git/ref/heads/{branch}",
                null,
                token,
                cancellationToken).ConfigureAwait(false);
            return GetString(reference, "object", "sha");
        }
        catch (GitHubApiException ex) when (ex.StatusCode == 404)
        {
            throw new GitHubApiException(404, $"分支 {branch} 不存在，或仓库 {owner}/{name} 无该分支。");
        }
    }

    private async Task<string> GetDefaultBranchAsync(
        string owner,
        string name,
        string token,
        CancellationToken cancellationToken)
    {
        try
        {
            var repo = await SendAsync(
                HttpMethod.Get,
                $"/repos/{owner}/{name}",
                null,
                token,
                cancellationToken).ConfigureAwait(false);
            return GetString(repo, "default_branch");
        }
        catch (GitHubApiException ex) when (ex.StatusCode == 404)
        {
            throw new GitHubApiException(404, $"仓库 {owner}/{name} 不存在，或 token 无权访问（404）。");
        }
    }

    private async Task<JsonElement> SendAsync(
        HttpMethod method,
        string path,
        object? body,
        string token,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(method, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (body is not null)
        {
            request.Content = new StringContent(
                JsonSerializer.Serialize(body, JsonOptions),
                Encoding.UTF8,
                "application/json");
        }

        using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        var payload = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new GitHubApiException(
                (int)response.StatusCode,
                DescribeFailure(method, path, (int)response.StatusCode, payload));
        }

        if (string.IsNullOrWhiteSpace(payload))
        {
            return default;
        }

        return JsonSerializer.Deserialize<JsonElement>(payload, JsonOptions);
    }

    private static string DescribeFailure(HttpMethod method, string path, int statusCode, string payload)
    {
        var hint = statusCode switch
        {
            401 => "GitHub token 无效或已过期（401）。",
            403 => "token 权限不足或触发限流（403）：fine-grained PAT 需授予该仓库 Contents: Read and write。",
            404 => $"资源不存在或无权访问（404）：{path}",
            409 => "远端引用已变化（409）。",
            422 => $"GitHub 拒绝了请求（422）：{TrimPayload(payload)}",
            _ => $"GitHub API 返回 {statusCode}：{TrimPayload(payload)}"
        };
        return $"{method.Method} {path} 失败。{hint}";
    }

    private static string TrimPayload(string payload)
    {
        var message = payload?.Trim() ?? string.Empty;
        return message.Length == 0 ? "(无响应体)" : message.Length > 300 ? message[..300] + "…" : message;
    }

    private static string GetString(JsonElement element, params string[] path)
    {
        var current = element;
        foreach (var segment in path)
        {
            if (current.ValueKind != JsonValueKind.Object || !current.TryGetProperty(segment, out var next))
            {
                throw new GitHubApiException(0, $"GitHub 响应缺少字段 {string.Join('.', path)}");
            }

            current = next;
        }

        return current.ToString() ?? string.Empty;
    }

    private static (string Owner, string Name) SplitRepo(SyncSettings settings)
    {
        var repo = settings.NormalizedRepo;
        var slash = repo.IndexOf('/');
        if (slash <= 0 || slash == repo.Length - 1)
        {
            throw new InvalidOperationException($"sync.repo 必须为 owner/name 格式，当前值: \"{repo}\"");
        }

        return (repo[..slash], repo[(slash + 1)..]);
    }

    private static HttpClient CreateDefaultClient()
    {
        var client = new HttpClient
        {
            BaseAddress = new Uri(ApiBase),
            Timeout = TimeSpan.FromSeconds(60)
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("GameHelper");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return client;
    }
}
