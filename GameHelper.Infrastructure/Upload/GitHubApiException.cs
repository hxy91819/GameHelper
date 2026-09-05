namespace GameHelper.Infrastructure.Upload;

/// <summary>GitHub API 调用失败（含可直接展示的中文错误说明）。</summary>
public sealed class GitHubApiException : Exception
{
    public int StatusCode { get; }

    public GitHubApiException(int statusCode, string message) : base(message)
    {
        StatusCode = statusCode;
    }
}
