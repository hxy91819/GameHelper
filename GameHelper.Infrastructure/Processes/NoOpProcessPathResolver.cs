using GameHelper.Core.Abstractions;

namespace GameHelper.Infrastructure.Processes;

public sealed class NoOpProcessPathResolver : IProcessPathResolver
{
    public string? TryResolveExecutablePath(int processId) => null;
}
