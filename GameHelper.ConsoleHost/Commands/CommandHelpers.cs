using System;

namespace GameHelper.ConsoleHost.Commands
{
    public static class CommandHelpers
    {
        public static void PrintUsage()
        {
            Console.WriteLine("GameHelper Console");
            Console.WriteLine("Usage:");
            Console.WriteLine("  interactive         启动全新的互动命令行体验（无命令时默认）");
            Console.WriteLine("  monitor [--config <path>] [--monitor-type <type>] [--debug]");
            Console.WriteLine("  config list [--config <path>] [--debug]");
            Console.WriteLine("  config add <exe> [--config <path>] [--debug]");
            Console.WriteLine("  config remove <dataKey> [--config <path>] [--debug]");
            Console.WriteLine("  stats [--game <name>] [--config <path>] [--debug]");
            Console.WriteLine("  migrate [--config <path>] [--csv <path>] [--dry-run] [--force]");
            Console.WriteLine("  convert-config");
            Console.WriteLine("  validate-config");
            Console.WriteLine();
            Console.WriteLine("Global options:");
            Console.WriteLine("  --config, -c       Override path to config.yml");
            Console.WriteLine("  --monitor-type     Process monitor type: WMI (default) or ETW");
            Console.WriteLine("                     ETW provides lower latency but requires admin privileges");
            Console.WriteLine("  --monitor-dry-run  Dry-run monitor flow without starting background services");
            Console.WriteLine("  --debug, -v        Enable verbose debug logging");
            Console.WriteLine("  --interactive      强制进入互动模式（等价于 interactive 命令）");
            Console.WriteLine();
            Console.WriteLine("Migrate command options:");
            Console.WriteLine("  --config <path>    配置文件路径（默认: %AppData%\\GameHelper\\config.yml）");
            Console.WriteLine("  --csv <path>       CSV 文件路径（默认: %AppData%\\GameHelper\\playtime.csv）");
            Console.WriteLine("  --dry-run          预览模式，不修改文件");
            Console.WriteLine("  --force            跳过确认提示");
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
