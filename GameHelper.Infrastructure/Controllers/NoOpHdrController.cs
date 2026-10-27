using GameHelper.Core.Abstractions;

namespace GameHelper.Infrastructure.Controllers;

public class NoOpHdrController : IHdrController
{
    public bool IsEnabled { get; private set; }

    /// <summary>The placeholder simulates HDR without physical display selection.</summary>
    public HdrAutomationAvailability GetAutomationAvailability() => HdrAutomationAvailability.Available;

    public bool TryGetDisplays(out IReadOnlyList<HdrDisplayInfo> displays)
    {
        displays = Array.Empty<HdrDisplayInfo>();
        return false;
    }

    public void Enable()
    {
        IsEnabled = true;
    }

    public void Disable()
    {
        IsEnabled = false;
    }
}
