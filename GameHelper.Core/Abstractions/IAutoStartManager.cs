namespace GameHelper.Core.Abstractions
{
    /// <summary>
    /// Controls whether GameHelper launches automatically when the operating system starts.
    /// </summary>
    public interface IAutoStartManager
    {
        /// <summary>
        /// Gets a value indicating whether auto-start management is supported on the current platform.
        /// </summary>
        bool IsSupported { get; }

        /// <summary>
        /// Returns whether GameHelper is currently registered for auto-start.
        /// </summary>
        bool IsEnabled();

        /// <summary>
        /// Enables or disables auto-start for GameHelper.
        /// </summary>
        /// <param name="enabled">True to enable auto-start; false to disable it.</param>
        void SetEnabled(bool enabled);
    }
}
