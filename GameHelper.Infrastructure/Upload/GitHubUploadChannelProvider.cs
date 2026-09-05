using System;
using GameHelper.Core.Abstractions;
using GameHelper.Core.Models;
using Microsoft.Extensions.Logging;

namespace GameHelper.Infrastructure.Upload;

/// <summary>
/// GitHub 渠道实现选择器：method=git 走本机 git.exe，method=api 走 REST API + token。
/// </summary>
public sealed class GitHubUploadChannelProvider : IStatsUploadChannelProvider
{
    private readonly GitHubGitChannel _gitChannel;
    private readonly GitHubApiChannel _apiChannel;

    public GitHubUploadChannelProvider(
        GitHubGitChannel? gitChannel = null,
        GitHubApiChannel? apiChannel = null)
    {
        _gitChannel = gitChannel ?? new GitHubGitChannel();
        _apiChannel = apiChannel ?? new GitHubApiChannel();
    }

    public IStatsUploadChannel GetChannel(SyncSettings settings)
    {
        if (!string.Equals(settings.Provider, "github", StringComparison.OrdinalIgnoreCase))
        {
            throw new NotSupportedException($"暂不支持的上传渠道: {settings.Provider}（当前仅支持 github）");
        }

        return settings.NormalizedMethod switch
        {
            "git" => _gitChannel,
            "api" => _apiChannel,
            var method => throw new NotSupportedException($"暂不支持的 sync.method: {method}（可选 git 或 api）")
        };
    }
}
