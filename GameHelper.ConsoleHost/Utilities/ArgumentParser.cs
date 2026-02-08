using System;
using System.Collections.Generic;
using System.Linq;

namespace GameHelper.ConsoleHost.Utilities
{
    public class ParsedArguments
    {
        public string? ConfigOverride { get; set; }
        public bool EnableDebug { get; set; }
        public string? MonitorType { get; set; }
        public bool MonitorDryRun { get; set; }
        public bool UseInteractiveShell { get; set; }
        public bool EnableWebServer { get; set; }
        public int WebServerPort { get; set; } = 5123;
        public string[] EffectiveArgs { get; set; } = Array.Empty<string>();
    }

    public static class ArgumentParser
    {
        // Extract optional global flags from args and build an effective argument list for commands
        // Supported: --config <path> | -c <path>, --debug | -v | --verbose, --monitor-type <type>, and --interactive/--menu
        public static ParsedArguments Parse(string[] args)
        {
            var result = new ParsedArguments();

            try
            {
                if (args != null && args.Length > 0)
                {
                    var list = new List<string>(args);

                    // Handle --config/-c
                    int idx = list.FindIndex(s => string.Equals(s, "--config", StringComparison.OrdinalIgnoreCase) ||
                                                  string.Equals(s, "-c", StringComparison.OrdinalIgnoreCase));
                    if (idx >= 0)
                    {
                        if (idx + 1 < list.Count)
                        {
                            result.ConfigOverride = list[idx + 1];
                            list.RemoveAt(idx + 1);
                            list.RemoveAt(idx);
                        }
                        else
                        {
                            Console.WriteLine("Missing path after --config/-c. Ignoring.");
                            list.RemoveAt(idx);
                        }
                    }

                    // Handle --debug/-v/--verbose (boolean flag)
                    int didx = list.FindIndex(s =>
                        string.Equals(s, "--debug", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(s, "-v", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(s, "--verbose", StringComparison.OrdinalIgnoreCase));
                    if (didx >= 0)
                    {
                        result.EnableDebug = true;
                        list.RemoveAt(didx);
                    }

                    // Handle --monitor-type
                    int midx = list.FindIndex(s => string.Equals(s, "--monitor-type", StringComparison.OrdinalIgnoreCase));
                    if (midx >= 0)
                    {
                        if (midx + 1 < list.Count)
                        {
                            result.MonitorType = list[midx + 1];
                            list.RemoveAt(midx + 1);
                            list.RemoveAt(midx);
                        }
                        else
                        {
                            Console.WriteLine("Missing type after --monitor-type. Ignoring.");
                            list.RemoveAt(midx);
                        }
                    }


                    // Handle --monitor-dry-run (boolean flag)
                    int dryRunIdx = list.FindIndex(s =>
                        string.Equals(s, "--monitor-dry-run", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(s, "--monitor-dryrun", StringComparison.OrdinalIgnoreCase));
                    if (dryRunIdx >= 0)
                    {
                        result.MonitorDryRun = true;
                        list.RemoveAt(dryRunIdx);
                    }

                    // Handle --interactive/--menu (boolean flag)
                    int iidx = list.FindIndex(s =>
                        string.Equals(s, "--interactive", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(s, "--menu", StringComparison.OrdinalIgnoreCase));
                    if (iidx >= 0)
                    {
                        result.UseInteractiveShell = true;
                        list.RemoveAt(iidx);
                    }

                    // Handle --web (boolean flag)
                    int widx = list.FindIndex(s =>
                        string.Equals(s, "--web", StringComparison.OrdinalIgnoreCase));
                    if (widx >= 0)
                    {
                        result.EnableWebServer = true;
                        list.RemoveAt(widx);
                    }

                    // Handle --port <number>
                    int pidx = list.FindIndex(s =>
                        string.Equals(s, "--port", StringComparison.OrdinalIgnoreCase));
                    if (pidx >= 0)
                    {
                        if (pidx + 1 < list.Count && int.TryParse(list[pidx + 1], out var port))
                        {
                            result.WebServerPort = port;
                            list.RemoveAt(pidx + 1);
                            list.RemoveAt(pidx);
                        }
                        else
                        {
                            Console.WriteLine("Missing or invalid port after --port. Using default 5123.");
                            list.RemoveAt(pidx);
                        }
                    }

                    result.EffectiveArgs = list.ToArray();
                }
            }
            catch
            {
            }

            return result;
        }
    }
}
