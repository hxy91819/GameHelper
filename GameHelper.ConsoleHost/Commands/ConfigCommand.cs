using System;
using System.Collections.Generic;
using System.Linq;
using GameHelper.Core.Abstractions;
using GameHelper.Core.Models;
using Microsoft.Extensions.DependencyInjection;

namespace GameHelper.ConsoleHost.Commands
{
    public static class ConfigCommand
    {
        public static void Run(IServiceProvider services, string[] args)
        {
            if (args.Length == 0) 
            { 
                CommandHelpers.PrintUsage(); 
                return; 
            }
            
            using var scope = services.CreateScope();
            var provider = scope.ServiceProvider.GetRequiredService<IConfigProvider>();
            var sub = args[0].ToLowerInvariant();
            var map = new Dictionary<string, GameConfig>(provider.Load(), StringComparer.OrdinalIgnoreCase);

            switch (sub)
            {
                case "list":
                    ListGames(map);
                    break;

                case "add":
                    AddGame(args, map, provider);
                    break;

                case "remove":
                    RemoveGame(args, map, provider);
                    break;
                    
                default:
                    CommandHelpers.PrintUsage();
                    break;
            }
        }

        private static void ListGames(Dictionary<string, GameConfig> map)
        {
            if (map.Count == 0)
            {
                Console.WriteLine("No games configured.");
                return;
            }
            
            foreach (var kv in map.OrderBy(k => k.Key, StringComparer.OrdinalIgnoreCase))
            {
                Console.WriteLine($"{kv.Key}  Enabled={kv.Value.IsEnabled}  HDR={kv.Value.HDREnabled}");
            }
        }

        private static void AddGame(string[] args, Dictionary<string, GameConfig> map, IConfigProvider provider)
        {
            if (args.Length < 2) 
            { 
                Console.WriteLine("Missing <exe>."); 
                return; 
            }
            
            var name = args[1];
            map[name] = new GameConfig { Name = name, IsEnabled = true, HDREnabled = true };
            provider.Save(map);
            Console.WriteLine($"Added {name}.");
        }

        private static void RemoveGame(string[] args, Dictionary<string, GameConfig> map, IConfigProvider provider)
        {
            if (args.Length < 2) 
            { 
                Console.WriteLine("Missing <exe>."); 
                return; 
            }
            
            var remove = args[1];
            if (map.Remove(remove))
            {
                provider.Save(map);
                Console.WriteLine($"Removed {remove}.");
            }
            else
            {
                Console.WriteLine($"Not found: {remove}");
            }
        }
    }
}