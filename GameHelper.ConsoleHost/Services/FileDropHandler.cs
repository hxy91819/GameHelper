using System.Runtime.InteropServices;

namespace GameHelper.ConsoleHost.Services;

internal static class FileDropHandler
{
    public static bool LooksLikeFilePaths(IReadOnlyCollection<string>? paths)
    {
        if (paths is null || paths.Count == 0)
        {
            return false;
        }

        return paths.All(path =>
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            {
                return false;
            }

            var extension = Path.GetExtension(path);
            return extension.Equals(".lnk", StringComparison.OrdinalIgnoreCase) ||
                   extension.Equals(".exe", StringComparison.OrdinalIgnoreCase) ||
                   extension.Equals(".url", StringComparison.OrdinalIgnoreCase);
        });
    }

    public static void TryShowMessageBox(string text, string caption)
    {
        try
        {
            MessageBoxW(IntPtr.Zero, text, caption, 0x00000040u);
        }
        catch
        {
            // Explorer launches have no console; notification is best-effort.
        }
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int MessageBoxW(IntPtr window, string text, string caption, uint type);
}
