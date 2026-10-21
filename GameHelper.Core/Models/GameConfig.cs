using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace GameHelper.Core.Models
{
    /// <summary>
    /// Represents a single game's automation settings and metadata.
    /// </summary>
    public class GameConfig
    {
        /// <summary><c>speedAttach</c> policy value: attach as soon as the first game process starts (legacy behavior).</summary>
        public const string SpeedAttachPolicyImmediate = "immediate";

        /// <summary><c>speedAttach</c> policy value: defer the attach while a mod loader is detected beside the executable (default).</summary>
        public const string SpeedAttachPolicyAuto = "auto";

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
        /// Optional Cheat Engine speed multiplier. A null value leaves the game speed unchanged.
        /// </summary>
        public double? SpeedMultiplier { get; set; }

        /// <summary>
        /// Optional policy controlling when the speed controller attaches Cheat Engine after the game starts.
        /// <c>auto</c> (default) defers the attach while a mod loader (BepInEx/MelonLoader) is detected beside the
        /// executable, until the loader reports its chainloader startup complete (timeout-bounded). <c>immediate</c>
        /// attaches on the first process activation regardless of mod loaders. A null value means auto.
        /// </summary>
        public string? SpeedAttach { get; set; }

        /// <summary>Optional launch entry; legacy absolute executable paths remain launchable.</summary>
        public GameLaunchOptions? Launch { get; set; }

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
