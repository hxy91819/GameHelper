using System;
using System.IO;
using GameHelper.Infrastructure.Providers;

namespace GameHelper.ConsoleHost.Commands
{
    public static class ConvertConfigCommand
    {
        public static void Run()
        {
            try
            {
                string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
                string dir = Path.Combine(appData, "GameHelper");
                string jsonPath = Path.Combine(dir, "config.json");
                string ymlPath = Path.Combine(dir, "config.yml");

                if (!File.Exists(jsonPath))
                {
                    Console.WriteLine($"No JSON config found at {jsonPath}");
                    return;
                }

                // Load from JSON
                var jsonProvider = new JsonConfigProvider(jsonPath);
                var data = jsonProvider.Load();
                Console.WriteLine($"Loaded {data.Count} entries from JSON.");

                // Save to YAML (overwrites if exists)
                var yamlProvider = new YamlConfigProvider(ymlPath);
                yamlProvider.Save(data);
                Console.WriteLine($"Written YAML to {ymlPath}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Failed to convert: {ex.Message}");
            }
        }
    }
}