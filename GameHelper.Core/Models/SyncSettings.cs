using System;

namespace GameHelper.Core.Models;

/// <summary>
/// 统计自动推送（sync）配置。存储在 config.yml 的 <c>sync:</c> 段。
/// </summary>
public sealed class SyncSettings
{
    /// <summary>推送内容在远端仓库内唯一的写入子目录，绝不触碰该目录之外的文件。</summary>
    public const string DefaultDirectory = "game-stats";

    /// <summary>自动推送的最小间隔（分钟），默认每天最多一次。</summary>
    public const int DefaultIntervalMinutes = 1440;

    /// <summary>用于从环境变量解析 token 的键名（仅 method=api 时需要）。</summary>
    public const string TokenEnvironmentVariable = "GAMEHELPER_GITHUB_TOKEN";

    public bool Enabled { get; set; }

    /// <summary>上传渠道标识，目前支持 <c>github</c>。</summary>
    public string Provider { get; set; } = "github";

    /// <summary>上传方式：<c>git</c>=本机 git.exe（复用已有凭据，默认）；<c>api</c>=GitHub REST API + token。</summary>
    public string Method { get; set; } = "git";

    /// <summary>目标仓库，格式 <c>owner/name</c>。</summary>
    public string Repo { get; set; } = string.Empty;

    /// <summary>目标分支；为空时 git 方式使用克隆的默认分支，api 方式使用仓库默认分支。</summary>
    public string? Branch { get; set; }

    /// <summary>仓库内写入的子目录，默认 <see cref="DefaultDirectory"/>。</summary>
    public string Directory { get; set; } = DefaultDirectory;

    /// <summary>访问令牌（仅 method=api）；为空时回退到环境变量 <see cref="TokenEnvironmentVariable"/>。</summary>
    public string? Token { get; set; }

    public int IntervalMinutes { get; set; } = DefaultIntervalMinutes;

    /// <summary>是否附带原始会话明细 CSV（含精确开始/结束时间，仅建议私有仓库开启）。</summary>
    public bool IncludeRawCsv { get; set; }

    /// <summary>
    /// 校验配置并归一化目录写法。返回 null 表示通过，否则返回可直接展示的错误信息。
    /// </summary>
    public string? Validate()
    {
        if (!string.Equals(Provider, "github", StringComparison.OrdinalIgnoreCase))
        {
            return $"暂不支持的上传渠道: {Provider}（当前仅支持 github）";
        }

        if (!string.Equals(Method, "git", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(Method, "api", StringComparison.OrdinalIgnoreCase))
        {
            return $"暂不支持的 sync.method: {Method}（可选 git 或 api）";
        }

        var repo = Repo?.Trim() ?? string.Empty;
        var slash = repo.IndexOf('/');
        if (slash <= 0 || slash == repo.Length - 1 || repo.IndexOf('/', slash + 1) >= 0)
        {
            return $"sync.repo 必须为 owner/name 格式，当前值: \"{Repo}\"";
        }

        var invalidRepoChars = false;
        foreach (var part in new[] { repo[..slash], repo[(slash + 1)..] })
        {
            foreach (var c in part)
            {
                if (!char.IsLetterOrDigit(c) && c is not ('-' or '.' or '_'))
                {
                    invalidRepoChars = true;
                    break;
                }
            }

            if (invalidRepoChars)
            {
                break;
            }
        }

        if (invalidRepoChars)
        {
            return $"sync.repo 含有不支持的字符: \"{Repo}\"";
        }

        var directory = NormalizedDirectory;
        if (string.IsNullOrWhiteSpace(directory))
        {
            return "sync.directory 不能为空";
        }

        foreach (var segment in directory.Split('/'))
        {
            if (segment is "." or "..")
            {
                return $"sync.directory 不允许相对路径段: \"{Directory}\"";
            }
        }

        if (IntervalMinutes < 5)
        {
            return $"sync.intervalMinutes 不能小于 5（当前值: {IntervalMinutes}）";
        }

        return null;
    }

    /// <summary>归一化后的仓库内目录（\ 转为 /，去掉首尾分隔符）。</summary>
    public string NormalizedDirectory
    {
        get
        {
            var value = (Directory ?? DefaultDirectory).Trim().Replace('\\', '/').Trim('/');
            return value.Length == 0 ? DefaultDirectory : value;
        }
    }

    /// <summary>归一化后的 method（小写）。</summary>
    public string NormalizedMethod =>
        string.Equals(Method, "api", StringComparison.OrdinalIgnoreCase) ? "api" : "git";

    /// <summary>归一化后的仓库（owner/name，去空白）。</summary>
    public string NormalizedRepo => Repo?.Trim() ?? string.Empty;

    /// <summary>归一化后的分支名（去空白，空字符串归为 null）。</summary>
    public string? NormalizedBranch
    {
        get
        {
            var value = Branch?.Trim();
            return string.IsNullOrEmpty(value) ? null : value;
        }
    }
}
