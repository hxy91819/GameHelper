using System.Collections.Generic;
using GameHelper.Core.Utilities;

namespace GameHelper.Core.Models
{
    /// <summary>
    /// Root configuration model that contains global application settings and game configurations.
    /// </summary>
    public class AppConfig
    {
        /// <summary>
        /// List of game configurations.
        /// </summary>
        public List<GameConfig>? Games { get; set; }

        /// <summary>
        /// Type of process monitor to use. If not specified, defaults to ETW (Event Tracing for Windows).
        /// ETW provides better performance and lower latency but requires administrator privileges.
        /// Users can explicitly set this to WMI for backward compatibility or non-admin environments.
        /// </summary>
        public ProcessMonitorType? ProcessMonitorType { get; set; }

        /// <summary>
        /// When enabled, the interactive shell will automatically start the monitor after launch.
        /// </summary>
        public bool AutoStartInteractiveMonitor { get; set; }

        /// <summary>
        /// When enabled, GameHelper will register itself to launch automatically on system startup.
        /// </summary>
        public bool LaunchOnSystemStartup { get; set; }

        /// <summary>
        /// Default speed multiplier applied when a game-specific override is not configured.
        /// </summary>
        public double DefaultSpeedMultiplier { get; set; } = SpeedDefaults.DefaultSpeedMultiplier;

        /// <summary>
        /// Global hotkey used to toggle speed for the current foreground game.
        /// </summary>
        public string SpeedToggleHotkey { get; set; } = SpeedDefaults.DefaultHotkey;
    }
}
