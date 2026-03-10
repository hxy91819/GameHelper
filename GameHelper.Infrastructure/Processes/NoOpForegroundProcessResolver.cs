using GameHelper.Core.Abstractions;
using GameHelper.Core.Models;

namespace GameHelper.Infrastructure.Processes;

public sealed class NoOpForegroundProcessResolver : IForegroundProcessResolver
{
    public ForegroundProcessInfo? GetForegroundProcess() => null;
}
