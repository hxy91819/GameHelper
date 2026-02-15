using System;
using System.ComponentModel.DataAnnotations;

namespace GameHelper.Core.Models
{
    /// <summary>
    /// Represents a single game's automation settings and metadata.
    /// </summary>
    public class GameConfig
    {
        /// <summary>
        /// Internal immutable identity for a config entry.
        /// This key is used for config CRUD and deduplication only, and MUST NOT be used as playtime aggregation key.
        /// </summary>
        [Required(AllowEmptyStrings = false)]
        public string EntryId { get; set; } = string.Empty;

        /// <summary>
        /// Stable business key used by playtime storage and aggregation.
        /// This key must be globally unique to avoid cross-game data aggregation collisions.
        /// </summary>
        [Required(AllowEmptyStrings = false)]
        public string DataKey { get; set; } = string.Empty;

        /// <summary>
        /// Optional absolute executable path for precise matching (L1).
        /// </summary>
        public string? ExecutablePath { get; set; }

        /// <summary>
        /// Optional executable name (e.g., "game.exe") used for fallback matching (L2).
        /// </summary>
        public string? ExecutableName { get; set; }

        /// <summary>
        /// Optional display-friendly name for UI surfaces.
        /// </summary>
        public string? DisplayName { get; set; }

        /// <summary>
        /// Whether this game participates in automation (monitoring/HDR/playtime).
        /// </summary>
        public bool IsEnabled { get; set; } = true;

        /// <summary>
        /// Desired HDR state while this game is active. Current HDR controller may be a NoOp.
        /// </summary>
        public bool HDREnabled { get; set; } = false;

        /// <summary>
        /// Legacy accessor retained for backward compatibility with older configuration serializers.
        /// Maps to <see cref="ExecutableName"/>.
        /// </summary>
        public string Name
        {
            get => ExecutableName ?? string.Empty;
            set => ExecutableName = value;
        }

        /// <summary>
        /// Legacy accessor retained for backward compatibility with older configuration serializers.
        /// Maps to <see cref="DisplayName"/>.
        /// </summary>
        public string? Alias
        {
            get => DisplayName;
            set => DisplayName = value;
        }
    }
}
