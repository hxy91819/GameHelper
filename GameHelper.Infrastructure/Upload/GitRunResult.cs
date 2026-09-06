namespace GameHelper.Infrastructure.Upload;

/// <summary>单次 git 命令执行结果。</summary>
public sealed record GitRunResult(int ExitCode, string StandardOutput, string StandardError)
{
    public bool Succeeded => ExitCode == 0;

    /// <summary>拼出可直接展示/记录的错误摘要（优先 stderr，截断到有限长度）。</summary>
    public string DescribeError()
    {
        var message = string.IsNullOrWhiteSpace(StandardError) ? StandardOutput : StandardError;
        message = message.Trim();
        if (message.Length > 500)
        {
            message = message[..500] + "…";
        }

        return message.Length == 0 ? $"git 退出码 {ExitCode}" : message;
    }
}
