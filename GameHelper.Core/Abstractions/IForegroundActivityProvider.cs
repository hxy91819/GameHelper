using GameHelper.Core.Models;

namespace GameHelper.Core.Abstractions;

/// <summary>Samples the current desktop, including foreground process identity and locked sessions.</summary>
public interface IForegroundActivityProvider
{
    ForegroundActivity Read();
}
