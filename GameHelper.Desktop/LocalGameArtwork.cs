using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace GameHelper.Desktop;

internal static class LocalGameArtwork
{
    public static BitmapSource? LoadPoster(string path)
    {
        try { return DecodePoster(File.ReadAllBytes(path)); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException or System.IO.FileFormatException or COMException) { return null; }
    }

    public static BitmapSource? LoadBanner(string path)
    {
        try { return DecodePoster(File.ReadAllBytes(path), 1280); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException or System.IO.FileFormatException or COMException) { return null; }
    }

    public static bool IsValidPoster(byte[] bytes) => DecodePoster(bytes) is not null;

    /// <summary>True when the file decodes to a wider-than-tall image usable in wide banner slots; oversized or broken files stay narrow.</summary>
    public static bool IsWideBannerImage(string path)
    {
        try
        {
            if (!File.Exists(path) || new FileInfo(path).Length is < 32 or > 5 * 1024 * 1024) return false;
            using var stream = File.OpenRead(path);
            var frame = BitmapDecoder.Create(stream, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None).Frames[0];
            return frame.PixelWidth >= 200 && frame.PixelWidth >= frame.PixelHeight;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException or System.IO.FileFormatException or COMException) { return false; }
    }

    private static BitmapSource? DecodePoster(byte[] bytes, int decodeWidth = 320)
    {
        if (bytes.Length < 32 || bytes.Length > 5 * 1024 * 1024) return null;
        try
        {
            using var stream = new MemoryStream(bytes, writable: false);
            var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);
            var frame = decoder.Frames[0];
            if (frame.PixelWidth is < 200 or > 8192 || frame.PixelHeight is < 200 or > 8192) return null;
            stream.Position = 0;
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.DecodePixelWidth = decodeWidth;
            bitmap.StreamSource = stream;
            bitmap.EndInit();
            bitmap.Freeze();
            return bitmap;
        }
        catch (Exception ex) when (ex is NotSupportedException or ArgumentException or System.IO.FileFormatException or IOException or COMException) { return null; }
    }

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

    public static ImageSource? LoadIcon(string executable)
    {
        try
        {
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
