using System;
using GameHelper.Infrastructure.Providers;
using GameHelper.Infrastructure.Validators;

namespace GameHelper.ConsoleHost.Commands
{
    public static class ValidateConfigCommand
    {
        public static void Run()
        {
            try
            {
                var provider = new YamlConfigProvider();
                string path = provider.ConfigPath;
                var result = YamlConfigValidator.Validate(path);

                Console.WriteLine($"Validating: {path}");
                Console.WriteLine($"Games: {result.GameCount}, Duplicates: {result.DuplicateCount}");
                
                if (result.Warnings.Count > 0)
                {
                    Console.WriteLine("Warnings:");
                    foreach (var w in result.Warnings) Console.WriteLine("  - " + w);
                }
                
                if (result.Errors.Count > 0)
                {
                    Console.WriteLine("Errors:");
                    foreach (var e in result.Errors) Console.WriteLine("  - " + e);
                }

                Console.WriteLine(result.IsValid ? "Config is valid." : "Config has errors.");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Failed to validate: {ex.Message}");
            }
        }
    }
}