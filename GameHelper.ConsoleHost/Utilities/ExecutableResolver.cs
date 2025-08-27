using System;
using System.IO;

namespace GameHelper.ConsoleHost.Utilities
{
    public static class ExecutableResolver
    {
        // Resolve input file to an executable path, supporting .lnk shortcuts
        public static string? TryResolveFromInput(string input)
        {
            if (string.IsNullOrWhiteSpace(input)) return null;
            var ext = Path.GetExtension(input);
            if (ext.Equals(".exe", StringComparison.OrdinalIgnoreCase)) return input;
            if (ext.Equals(".lnk", StringComparison.OrdinalIgnoreCase))
            {
                var target = ResolveShortcutTargetViaWsh(input);
                return target;
            }
            return null;
        }

        // Use WScript.Shell COM to resolve .lnk target without compile-time COM references
        private static string? ResolveShortcutTargetViaWsh(string lnkPath)
        {
            try
            {
                var shellType = Type.GetTypeFromProgID("WScript.Shell");
                if (shellType == null) return null;
                var shell = Activator.CreateInstance(shellType);
                var shortcut = shellType.InvokeMember("CreateShortcut", System.Reflection.BindingFlags.InvokeMethod, null, shell, new object[] { lnkPath });
                var target = shortcut?.GetType().InvokeMember("TargetPath", System.Reflection.BindingFlags.GetProperty, null, shortcut, null) as string;
                return target;
            }
            catch { return null; }
        }
    }
}