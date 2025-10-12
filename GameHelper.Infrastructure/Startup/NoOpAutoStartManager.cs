using GameHelper.Core.Abstractions;

namespace GameHelper.Infrastructure.Startup
{
    /// <summary>
    /// Fallback implementation for platforms where auto-start cannot be managed.
    /// </summary>
    public sealed class NoOpAutoStartManager : IAutoStartManager
    {
        public bool IsSupported => false;

        public bool IsEnabled() => false;

        public void SetEnabled(bool enabled)
        {
            // Intentionally no-op.
        }
    }
}
