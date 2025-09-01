using System.Collections.Generic;

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
        /// Type of process monitor to use. If not specified, defaults to WMI for backward compatibility.
        /// </summary>
        public ProcessMonitorType? ProcessMonitorType { get; set; }
    }
}