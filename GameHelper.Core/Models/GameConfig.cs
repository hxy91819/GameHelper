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
        /// Optional absolute executable path derived from <see cref="Executable"/>.
        /// </summary>
        [JsonIgnore]
        public string? ExecutablePath
        {
            get
            {
                var executable = NormalizeExecutable(Executable);
                return LooksLikePath(executable) ? executable : null;
            }
            set
            {
                if (!string.IsNullOrWhiteSpace(value))
                {
                    Executable = value.Trim();
                    return;
                }

                if (LooksLikePath(Executable))
                {
                    Executable = null;
                }
            }
        }

        /// <summary>
        /// Executable file name derived from <see cref="Executable"/>.
        /// </summary>
        [JsonIgnore]
        public string? ExecutableName
        {
            get
            {
                var executable = NormalizeExecutable(Executable);
                if (string.IsNullOrWhiteSpace(executable))
                {
                    return null;
                }

                return LooksLikePath(executable) ? Path.GetFileName(executable) : executable;
            }
            set
            {
                if (string.IsNullOrWhiteSpace(value))
                {
                    if (!LooksLikePath(Executable))
                    {
                        Executable = null;
                    }

                    return;
                }

                if (!LooksLikePath(Executable))
                {
                    Executable = value.Trim();
                }
            }
        }

        [JsonIgnore]
        public bool HDREnabled
        {
            get => HdrEnabled;
            set => HdrEnabled = value;
        }

        [JsonIgnore]
        public string Name
        {
            get => ExecutableName ?? string.Empty;
            set => ExecutableName = value;
        }

        [JsonIgnore]
        public string? Alias
        {
            get => DisplayName;
            set => DisplayName = value;
        }

        private static string? NormalizeExecutable(string? executable) =>
            string.IsNullOrWhiteSpace(executable) ? null : executable.Trim();

        private static bool LooksLikePath(string? value)
        {
            return !string.IsNullOrWhiteSpace(value) &&
                   (Path.IsPathFullyQualified(value) ||
                    value.Contains(Path.DirectorySeparatorChar) ||
                    value.Contains(Path.AltDirectorySeparatorChar));
        }
    }
}
