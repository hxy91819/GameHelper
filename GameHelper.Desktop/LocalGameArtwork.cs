using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;

namespace GameHelper.Desktop;

internal static class LocalGameArtwork
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ShellFileInfo
    {
        public IntPtr Icon;
        public int IconIndex;
        public uint Attributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string DisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)] public string TypeName;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SHGetFileInfo(string path, uint attributes, ref ShellFileInfo info, uint size, uint flags);
    [DllImport("user32.dll")] private static extern bool DestroyIcon(IntPtr icon);

    public static ImageSource? Load(string executable, string launchTarget)
    {
        try
        {
            // Local Steam cache only; no scraping, network requests, or downloads.
            const string prefix = "steam://rungameid/";
            if (launchTarget.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                var appId = launchTarget[prefix.Length..].TrimEnd('/');
                if (appId.Length > 0 && appId.All(char.IsAsciiDigit))
                {
                    var steam = Registry.GetValue(@"HKEY_CURRENT_USER\Software\Valve\Steam", "SteamPath", null) as string;
                    if (steam is not null)
                    {
                        var cover = Path.Combine(steam, "appcache", "librarycache", appId + "_library_600x900.jpg");
                        if (File.Exists(cover))
                        {
                            var bitmap = new BitmapImage();
                            bitmap.BeginInit();
                            bitmap.CacheOption = BitmapCacheOption.OnLoad;
                            bitmap.DecodePixelWidth = 320;
                            bitmap.UriSource = new Uri(cover);
                            bitmap.EndInit();
                            bitmap.Freeze();
                            return bitmap;
                        }
                    }
                }
            }
            if (!Path.IsPathFullyQualified(executable) || executable.StartsWith(@"\\", StringComparison.Ordinal) || !File.Exists(executable)) return null;
            var info = new ShellFileInfo();
            SHGetFileInfo(executable, 0, ref info, (uint)Marshal.SizeOf<ShellFileInfo>(), 0x100);
            if (info.Icon == IntPtr.Zero) return null;
            try
            {
                var bitmap = Imaging.CreateBitmapSourceFromHIcon(info.Icon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                bitmap.Freeze();
                return bitmap;
            }
            finally { DestroyIcon(info.Icon); }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or COMException)
        {
            return null;
        }
    }
}
