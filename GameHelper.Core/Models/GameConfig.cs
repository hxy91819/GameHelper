namespace GameHelper.Core.Models
{
    /// <summary>
    /// Represents a single game's automation settings keyed by executable name.
    /// </summary>
    public class GameConfig
    {
        /// <summary>
        /// Executable name (e.g., "game.exe"). Used as the key in configuration.
        /// </summary>
        public string Name { get; set; } = string.Empty;

        /// <summary>
        /// Optional display alias. Purely cosmetic and not used for matching.
        /// </summary>
        public string? Alias { get; set; }

        /// <summary>
        /// Whether this game participates in automation (monitoring/HDR/playtime).
        /// </summary>
        public bool IsEnabled { get; set; } = true;

        /// <summary>
        /// Desired HDR state while this game is active. Current HDR controller may be a NoOp.
        /// </summary>
        public bool HDREnabled { get; set; } = false;
    }
}
