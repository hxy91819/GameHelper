using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace GameHelper.Core.Models
{
    /// <summary>
    /// Represents a single game's automation settings and metadata.
    /// </summary>
    public class GameConfig
    {
        /// <summary>
        /// Stable business key used by playtime storage and aggregation.
        /// This key must be globally unique to avoid cross-game data aggregation collisions.
        /// </summary>
        [Required(AllowEmptyStrings = false)]
        public string DataKey { get; set; } = string.Empty;

        /// <summary>
        /// Executable identity used for matching. This can be either an absolute executable path or an executable file name.
        /// </summary>
        [Required(AllowEmptyStrings = false)]
        public string? Executable { get; set; }

        /// <summary>
        /// Optional display-friendly name for UI surfaces. Falls back to <see cref="DataKey"/> when omitted.
        /// </summary>
        public string? DisplayName { get; set; }

        /// <summary>
        /// Whether this game participates in automation (monitoring/HDR/playtime).
        /// </summary>
        public bool IsEnabled { get; set; } = true;

        /// <summary>
        /// Desired HDR state while this game is active. Current HDR controller may be a NoOp.
        /// </summary>
        public bool HdrEnabled { get; set; } = false;

        /// <summary>
        /// Immutable identity view derived from <see cref="Executable"/>.
        /// </summary>
        [JsonIgnore]
        public ExecutableIdentity? ExecutableIdentity =>
            global::GameHelper.Core.Models.ExecutableIdentity.TryCreate(Executable, out var identity) ? identity : null;

        /// <summary>
        /// Optional absolute executable path derived from <see cref="Executable"/>.
        /// </summary>
        [JsonIgnore]
        public string? ExecutablePath => ExecutableIdentity?.Path;

        /// <summary>
        /// Executable file name derived from <see cref="Executable"/>.
        /// </summary>
        [JsonIgnore]
        public string? ExecutableName => ExecutableIdentity?.Name;
    }
}
