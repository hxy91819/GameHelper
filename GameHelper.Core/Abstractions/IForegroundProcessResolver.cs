using GameHelper.Core.Models;

namespace GameHelper.Core.Abstractions;

public interface IForegroundProcessResolver
{
    ForegroundProcessInfo? GetForegroundProcess();
}
