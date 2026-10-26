using GameHelper.Core.Models;

namespace GameHelper.Core.Abstractions
{
    /// <summary>
    /// Controls system-wide HDR state. Implementations may be a no-op placeholder
    /// or use OS/hardware specific APIs to toggle HDR on/off.
    /// </summary>
    public interface IHdrController
    {
        /// <summary>
        /// Current HDR state as perceived by the controller.
        /// </summary>
        bool IsEnabled { get; }

        /// <summary>
        /// Queries the current HDR state, including changes made outside GameHelper.
        /// Returns false when the state cannot be determined; callers must not assume SDR.
        /// </summary>
        bool TryGetEnabled(out bool enabled)
        {
            enabled = IsEnabled;
            return true;
        }

        /// <summary>
        /// Enables HDR. Safe to call if already enabled.
        /// </summary>
        void Enable();

        /// <summary>Updates display selection without changing the current system HDR state.</summary>
        void Configure(HdrSettings? settings) { }

        /// <summary>Distinguishes deliberate display exclusion from unsupported hardware or query failure.</summary>
        HdrAutomationAvailability GetAutomationAvailability() => HdrAutomationAvailability.Available;

        /// <summary>Queries all active targets, including excluded displays; false means unknown.</summary>
        bool TryGetDisplays(out IReadOnlyList<HdrDisplayInfo> displays)
        {
            displays = Array.Empty<HdrDisplayInfo>();
            return false;
        }

        /// <summary>
        /// Disables HDR. Safe to call if already disabled.
        /// </summary>
        void Disable();
    }
}
