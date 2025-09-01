using System;

namespace GameHelper.ConsoleHost.Commands
{
    public static class CommandHelpers
    {
        public static void PrintUsage()
        {
            Console.WriteLine("GameHelper Console");
            Console.WriteLine("Usage:");
            Console.WriteLine("  monitor [--config <path>] [--monitor-type <type>] [--debug]");
            Console.WriteLine("  config list [--config <path>] [--debug]");
            Console.WriteLine("  config add <exe> [--config <path>] [--debug]");
            Console.WriteLine("  config remove <exe> [--config <path>] [--debug]");
            Console.WriteLine("  stats [--game <name>] [--config <path>] [--debug]");
            Console.WriteLine("  convert-config");
            Console.WriteLine("  validate-config");
            Console.WriteLine();
            Console.WriteLine("Global options:");
            Console.WriteLine("  --config, -c       Override path to config.yml");
            Console.WriteLine("  --monitor-type     Process monitor type: WMI (default) or ETW");
            Console.WriteLine("                     ETW provides lower latency but requires admin privileges");
            Console.WriteLine("  --debug, -v        Enable verbose debug logging");
        }

        public static void PrintBuildInfo(bool debug)
        {
            try
            {
                string? path = System.Reflection.Assembly.GetEntryAssembly()?.Location;
                if (string.IsNullOrWhiteSpace(path) || !System.IO.File.Exists(path))
                {
                    try { path = System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName; } catch { }
                }
                DateTime? ts = null;
                if (!string.IsNullOrWhiteSpace(path) && System.IO.File.Exists(path))
                {
                    ts = System.IO.File.GetLastWriteTime(path);
                }
                if (ts.HasValue)
                {
                    Console.WriteLine($"Build time: {ts.Value:yyyy-MM-dd HH:mm:ss}");
                }
                Console.WriteLine($"Log level: {(debug ? "Debug" : "Information")}");
            }
            catch { }
        }
    }
}