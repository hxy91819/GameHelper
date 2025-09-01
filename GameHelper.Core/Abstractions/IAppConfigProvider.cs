using GameHelper.Core.Models;

namespace GameHelper.Core.Abstractions
{
    /// <summary>
    /// Provides access to application-wide configuration including global settings and game configurations.
    /// </summary>
    public interface IAppConfigProvider
    {
        /// <summary>
        /// Loads the complete application configuration including global settings.
        /// </summary>
        AppConfig LoadAppConfig();

        /// <summary>
        /// Saves the complete application configuration including global settings.
        /// </summary>
        void SaveAppConfig(AppConfig appConfig);
    }
}