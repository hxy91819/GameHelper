using System;
using System.IO;
using GameHelper.Core.Utilities;
using GameHelper.Infrastructure.Providers;

namespace GameHelper.ConsoleHost.Commands
{
    public static class ConvertConfigCommand
    {
        public static void Run()
        {
            try
            {
                string dir = AppDataPath.GetGameHelperDirectory();
                string jsonPath = Path.Combine(dir, "config.json");
                string ymlPath = AppDataPath.GetConfigPath();

                if (!File.Exists(jsonPath))
                {
                    Console.WriteLine($"No JSON config found at {jsonPath}");
                    return;
                }

                // Load from JSON
                var jsonProvider = new JsonConfigProvider(jsonPath);
                var data = jsonProvider.Read();
                Console.WriteLine($"Loaded {data.Games.Count} entries from JSON.");

                // Save to YAML (overwrites if exists)
                var yamlProvider = new YamlConfigProvider(ymlPath);
                yamlProvider.Change(config =>
                {
                    config.ProcessMonitorType = data.ProcessMonitorType;
                    config.AutoStartInteractiveMonitor = data.AutoStartInteractiveMonitor;
                    config.LaunchOnSystemStartup = data.LaunchOnSystemStartup;
                    config.Games = data.Games;
                });
                Console.WriteLine($"Written YAML to {ymlPath}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Failed to convert: {ex.Message}");
            }
        }
    }
}
