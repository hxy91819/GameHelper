using System;
using GameHelper.ConsoleHost.Utilities;

namespace GameHelper.ConsoleHost.Commands;

public static class CommandHelpers
{
    public static void PrintUsage()
    {
        Console.WriteLine("GameHelper Console");
        Console.WriteLine("Usage:");
        Console.WriteLine("  (no command)        Open the GameHelper library window");
        Console.WriteLine("  monitor [--config <path>] [--monitor-type <type>] [--debug]");
        Console.WriteLine("  config list [--config <path>] [--debug]");
        Console.WriteLine("  config add <exe|path-to-exe> [--config <path>] [--debug]");
        Console.WriteLine("  config import-steam [--config <path>] [--debug]");
        Console.WriteLine("  config remove <dataKey> [--config <path>] [--debug]");
        Console.WriteLine("  stats [--game <name>] [--config <path>] [--debug]");
        Console.WriteLine("  sync now [--force] / sync test / sync status");
        Console.WriteLine("                      Stats sync configuration and manual triggers (see docs/guides/sync.md)");
        Console.WriteLine("  validate-config");
        Console.WriteLine();
        Console.WriteLine("Global options:");
        Console.WriteLine("  --config, -c       Override path to config.yml");
        Console.WriteLine("  --monitor-type     Process monitor type: ETW (default) or WMI");
        Console.WriteLine("                     ETW provides lower latency but may require admin privileges");
        Console.WriteLine("  --monitor-dry-run  Dry-run monitor flow without starting background services");
        Console.WriteLine("  --debug, -v        Enable verbose debug logging");
    }

    public static void PrintBuildInfo(bool debug)
    {
        try
        {
            var buildTime = BuildInfoHelper.GetBuildTimeDescription();
            if (!string.Equals(buildTime, "unknown", StringComparison.OrdinalIgnoreCase))
            {
                Console.WriteLine($"Build time: {buildTime}");
            }
            Console.WriteLine($"Log level: {(debug ? "Debug" : "Information")}");
        }
        catch { }
    }
}
