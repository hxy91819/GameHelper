using System;
using System.Collections.Generic;
using System.Linq;

namespace GameHelper.ConsoleHost.Utilities
{
    public class ParsedArguments
    {
        public string? ConfigOverride { get; set; }
        public bool EnableDebug { get; set; }
        public string[] EffectiveArgs { get; set; } = Array.Empty<string>();
    }

    public static class ArgumentParser
    {
        // Extract optional global flags from args and build an effective argument list for commands
        // Supported: --config <path> | -c <path>, and --debug | -v | --verbose
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
                    
                    result.EffectiveArgs = list.ToArray();
                }
            }
            catch { }

            return result;
        }
    }
}