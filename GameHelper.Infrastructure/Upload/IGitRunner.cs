using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace GameHelper.Infrastructure.Upload;

/// <summary>git 命令执行抽象，便于单元测试替换。</summary>
public interface IGitRunner
{
    Task<GitRunResult> RunAsync(
        string? workingDirectory,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken = default);
}
